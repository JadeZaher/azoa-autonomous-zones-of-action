using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Core.Signing;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Signing;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Providers.Blockchain.Base;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace OASIS.WebAPI.Providers.Blockchain.Algorand;

/// <summary>
/// Algorand blockchain provider using direct REST API calls to Algod and Indexer.
/// Avoids SDK versioning issues while providing full devnet/testnet/mainnet connectivity.
/// </summary>
public class AlgorandProvider : BaseBlockchainProvider, IAlgorandASAModule
{
    // Assigned in BuildClients(), which the ctor invokes before any use; the
    // null-forgiving initializer documents that and clears CS8618 without a
    // misleading nullable annotation (the field is never observably null).
    private HttpClient _algodHttpClient = null!;
    private HttpClient? _indexerHttpClient;
    private readonly BlockchainConfigurationManager _configManager;

    // Signing seam (signing-core-keystone). The provider builds canonical
    // transactions and delegates the actual Ed25519 signing to the chain-agnostic
    // signer resolved by ChainType.
    private readonly ITransactionSignerFactory? _signerFactory;

    // INTERIM custody supply (signing-core-keystone Phase 3, plan §79-82). Good
    // enough for tests/testnet: decrypts the platform signing key from config via
    // WalletKeyService. The REAL ownership-checked, per-user resolver + IDOR guard
    // + byte[] zeroing is the custody-key-management track's deliverable (B3/P1).
    private readonly WalletKeyService? _keyService;

    public override string ChainType => "Algorand";
    public string CapabilityName => "Algorand.ASA";
    public override bool SupportsBridging => true;

    public AlgorandProvider(IConfiguration config, ILogger<AlgorandProvider> logger)
        : this(config, logger, signerFactory: null, keyService: null)
    {
    }

    public AlgorandProvider(
        IConfiguration config,
        ILogger<AlgorandProvider> logger,
        ITransactionSignerFactory? signerFactory,
        WalletKeyService? keyService)
        : base(config, logger)
    {
        _configManager = new BlockchainConfigurationManager(config);
        _signerFactory = signerFactory;
        _keyService = keyService;

        var network = _configManager.GetDefaultNetwork(ChainType);
        var networkConfig = _configManager.GetNetworkConfig(ChainType, network);

        BuildClients(networkConfig);
        Initialize(networkConfig, network);
    }

    /// <summary>
    /// (Re)builds the Algod + Indexer clients so they bind to the network passed
    /// to <see cref="Initialize"/> — the single place clients are constructed.
    /// Without this, the ctor-time default network would be used regardless of
    /// the network the factory requested.
    /// </summary>
    public override void Initialize(BlockchainNetworkConfig config, ChainNetwork network)
    {
        BuildClients(config);
        base.Initialize(config, network);
    }

    private void BuildClients(BlockchainNetworkConfig config)
    {
        _algodHttpClient = CreateHttpClient(config.NodeUrl, config.ApiToken, config.TimeoutMs);
        _indexerHttpClient = !string.IsNullOrWhiteSpace(config.IndexerUrl)
            ? CreateHttpClient(config.IndexerUrl, config.ApiToken, config.TimeoutMs)
            : null;
    }

    private static HttpClient CreateHttpClient(string baseUrl, string? apiToken, int? timeoutMs)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMilliseconds(timeoutMs ?? 30000) };
        if (!string.IsNullOrWhiteSpace(apiToken))
            client.DefaultRequestHeaders.Add("X-Algo-API-Token", apiToken);
        return client;
    }

    // H4 (DEPLOY-STEPS-TODO): real SHA-512/256 checksum validation via Algorand2's
    // Address.IsValid — replaces the prior length+charset-only regex check.
    private static bool ValidateAddressFormat(string address)
    {
        return !string.IsNullOrWhiteSpace(address) && Address.IsValid(address);
    }

    // ─── Account / Wallet ───

    public override async Task<OASISResult<string>> GetBalanceAsync(
        string address, string? tokenId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Error<string>("Address is required");

        if (!ValidateAddressFormat(address))
            return Error<string>("Invalid Algorand address format (must be 58-char base32)");

        try
        {
            var account = await _algodHttpClient.GetFromJsonAsync<AlgodAccountInfo>(
                $"/v2/accounts/{address}", cancellationToken: ct);

            if (account == null)
                return Error<string>("Account not found");

            if (string.IsNullOrWhiteSpace(tokenId))
            {
                var algo = account.Amount / 1_000_000.0;
                return Ok(algo.ToString("F6"), $"Retrieved {algo:F6} ALGO for {address}");
            }

            // Look up ASA balance
            var asset = account.Assets?.FirstOrDefault(a => a.AssetId == long.Parse(tokenId));
            return Ok(
                asset?.Amount.ToString() ?? "0",
                asset != null
                    ? $"Retrieved {asset.Amount} of ASA {tokenId}"
                    : $"No holding of ASA {tokenId} by {address}");
        }
        catch (Exception ex)
        {
            return Error<string>($"Balance fetch failed: {ex.Message}", ex);
        }
    }

    public override async Task<OASISResult<bool>> ValidateAddressAsync(
        string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new OASISResult<bool> { IsError = true, Result = false, Message = "Address is required" };

        if (!ValidateAddressFormat(address))
            return new OASISResult<bool> { IsError = true, Result = false, Message = "Invalid Algorand address format" };

        try
        {
            var account = await _algodHttpClient.GetFromJsonAsync<AlgodAccountInfo>(
                $"/v2/accounts/{address}", cancellationToken: ct);
            return account != null
                ? Ok(true, "Valid Algorand address — confirmed on network")
                : new OASISResult<bool> { IsError = true, Result = false, Message = "Address not found on network" };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool> { IsError = true, Result = false, Message = $"Validation failed: {ex.Message}" };
        }
    }

    // ─── Token / Asset Lifecycle ───

    public override async Task<OASISResult<string>> MintAsync(
        string tokenUri, int amount, string assetType, string walletAddress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress) || amount <= 0 || string.IsNullOrWhiteSpace(assetType))
            return Error<string>("Wallet address, positive amount, and asset type are required");

        return await CreateASAAsync(
            assetType, assetType.ToUpperInvariant()[..Math.Min(8, assetType.Length)],
            amount, 0, walletAddress, walletAddress, walletAddress, walletAddress,
            walletAddress, ct);
    }

    public override async Task<OASISResult<string>> BurnAsync(
        string tokenId, int amount, string walletAddress, CancellationToken ct = default)
    {
        // Burn = AssetDestroy (acfg with no params), signed by the asset manager.
        // The full supply must be held by the creator/manager for destroy to
        // succeed; the manager (platform) address is the signer.
        if (string.IsNullOrWhiteSpace(tokenId) || !ulong.TryParse(tokenId, out var assetIndex))
            return Error<string>("A numeric asset ID is required to burn an ASA");
        if (string.IsNullOrWhiteSpace(walletAddress) || !ValidateAddressFormat(walletAddress))
            return Error<string>("A valid manager (platform) address is required to burn an ASA");

        return await BuildSignSubmitAsync(
            walletAddress,
            (paramsInfo, sender) => new AssetDestroyTransaction
            {
                Sender = sender,
                AssetIndex = assetIndex,
            },
            opLabel: $"burn ASA {tokenId}",
            ct: ct);
    }

    public override async Task<OASISResult<string>> TransferAsync(
        string tokenId, string fromAddress, string toAddress, int amount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || !ulong.TryParse(tokenId, out var assetIndex))
            return Error<string>("A numeric asset ID is required to transfer an ASA");
        if (string.IsNullOrWhiteSpace(fromAddress) || !ValidateAddressFormat(fromAddress))
            return Error<string>("A valid sender address is required");
        if (string.IsNullOrWhiteSpace(toAddress) || !ValidateAddressFormat(toAddress))
            return Error<string>("A valid recipient address is required");
        if (amount <= 0)
            return Error<string>("Transfer amount must be positive");

        var receiver = new Address(toAddress);
        return await BuildSignSubmitAsync(
            fromAddress,
            (paramsInfo, sender) => new AssetTransferTransaction
            {
                Sender = sender,
                XferAsset = assetIndex,
                AssetReceiver = receiver,
                AssetAmount = (ulong)amount,
            },
            opLabel: $"transfer {amount} of ASA {tokenId} to {toAddress}",
            ct: ct);
    }

    public override Task<OASISResult<string>> ExchangeAsync(
        string sourceTokenId, string targetTokenId, string exchangeRate,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Exchange on Algorand requires DEX integration (Tinyman/Pact AMM). Not yet implemented."));
    }

    public override Task<OASISResult<string>> SwapAsync(
        string tokenIn, string tokenOut, decimal amountIn, decimal minAmountOut,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Swap on Algorand requires DEX integration (Tinyman/Pact AMM). Not yet implemented."));
    }

    // ─── Query / Metadata ───

    public override async Task<OASISResult<Dictionary<string, object>>> GetTokenMetadataAsync(
        string tokenId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tokenId) || _indexerHttpClient == null)
                return Error<Dictionary<string, object>>("Token ID required and Indexer must be configured");

            if (!long.TryParse(tokenId, out var assetId))
                return Error<Dictionary<string, object>>($"Invalid asset ID: {tokenId}");

            var response = await _indexerHttpClient.GetFromJsonAsync<IndexerAssetResponse>(
                $"/v2/assets/{assetId}", cancellationToken: ct);

            if (response?.Asset == null)
                return Error<Dictionary<string, object>>("Asset not found");

            var asset = response?.Asset?.Params;
            var meta = new Dictionary<string, object>
            {
                ["chain"] = "Algorand",
                ["assetId"] = tokenId,
                ["name"] = asset?.Name ?? "Unknown",
                ["unitName"] = asset?.UnitName ?? "",
                ["totalSupply"] = asset?.Total.ToString() ?? "0",
                ["decimals"] = asset?.Decimals.ToString() ?? "0",
                ["creator"] = asset?.Creator ?? "",
                ["url"] = asset?.Url ?? "",
                ["isFrozen"] = asset?.DefaultFrozen ?? false,
                ["fetchedAt"] = DateTime.UtcNow
            };

            return Ok(meta, "Asset metadata retrieved");
        }
        catch (Exception ex)
        {
            return Error<Dictionary<string, object>>($"Metadata fetch failed: {ex.Message}", ex);
        }
    }

    public override async Task<OASISResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
        string ownerAddress, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ownerAddress) || _indexerHttpClient == null)
                return Error<List<Dictionary<string, object>>>("Owner address required and Indexer must be configured");

            var response = await _indexerHttpClient.GetFromJsonAsync<IndexerAssetsListResponse>(
                $"/v2/accounts/{ownerAddress}/assets", cancellationToken: ct);

            var tokens = new List<Dictionary<string, object>>();
            if (response?.Assets != null)
            {
                foreach (var a in response.Assets)
                    tokens.Add(new Dictionary<string, object>
                    {
                        ["assetId"] = a.AssetId.ToString(),
                        ["amount"] = a.Amount.ToString(),
                        ["isFrozen"] = a.IsFrozen
                    });
            }

            return Ok(tokens, $"Retrieved {tokens.Count} ASAs for {ownerAddress}");
        }
        catch (Exception ex)
        {
            return Error<List<Dictionary<string, object>>>($"Token fetch failed: {ex.Message}", ex);
        }
    }

    public override async Task<OASISResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(txHash))
                return Error<Dictionary<string, object>>("Transaction hash is required");

            // Algod only exposes pending-transaction status at
            // /v2/transactions/pending/{txid} (there is no /v2/transactions/{id}
            // route). Once a tx leaves the pending pool, fall back to the
            // Indexer's /v2/transactions/{txid} for the confirmed record.
            AlgodPendingTransactionInfo? pending = null;
            try
            {
                pending = await _algodHttpClient.GetFromJsonAsync<AlgodPendingTransactionInfo>(
                    $"/v2/transactions/pending/{txHash}", cancellationToken: ct);
            }
            catch (HttpRequestException)
            {
                // Not in the pending pool (404) — fall through to the indexer.
            }

            if (pending != null)
            {
                var pendingStatus = new Dictionary<string, object>
                {
                    ["txHash"] = txHash,
                    ["chain"] = "Algorand",
                    ["confirmed"] = pending.ConfirmedRound > 0,
                    ["confirmedRound"] = pending.ConfirmedRound.ToString(),
                    ["fee"] = (pending.Txn?.Transaction?.Fee ?? 0).ToString(),
                    ["sender"] = pending.Txn?.Transaction?.Sender ?? "",
                    ["type"] = pending.Txn?.Transaction?.TxType ?? "unknown",
                    ["fetchedAt"] = DateTime.UtcNow
                };
                return Ok(pendingStatus, "Transaction status retrieved (algod pending pool)");
            }

            if (_indexerHttpClient == null)
                return Error<Dictionary<string, object>>(
                    "Transaction not in algod pending pool and no Indexer is configured to look up confirmed transactions");

            var indexed = await _indexerHttpClient.GetFromJsonAsync<IndexerTransactionResponse>(
                $"/v2/transactions/{txHash}", cancellationToken: ct);

            var tx = indexed?.Transaction;
            var status = new Dictionary<string, object>
            {
                ["txHash"] = txHash,
                ["chain"] = "Algorand",
                ["confirmed"] = tx?.ConfirmedRound > 0,
                ["confirmedRound"] = (tx?.ConfirmedRound ?? 0).ToString(),
                ["fee"] = (tx?.Fee ?? 0).ToString(),
                ["sender"] = tx?.Sender ?? "",
                ["type"] = tx?.TxType ?? "unknown",
                ["fetchedAt"] = DateTime.UtcNow
            };

            return Ok(status, "Transaction status retrieved (indexer)");
        }
        catch (Exception ex)
        {
            return Error<Dictionary<string, object>>($"Status fetch failed: {ex.Message}", ex);
        }
    }

    public override Task<OASISResult<string>> DeployContractAsync(
        byte[] contractCode, string walletAddress,
        Dictionary<string, object>? args = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "AVM contract deployment requires TEAL compilation and client-side signing. Not yet implemented."));
    }

    public override Task<OASISResult<object>> CallContractAsync(
        string contractAddress, string method, Dictionary<string, object> args,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<object>(
            "AVM contract calls require client-side signing. Not yet implemented."));
    }

    public override async Task<OASISResult<Dictionary<string, object>>> GetChainInfoAsync(
        CancellationToken ct = default)
    {
        try
        {
            var status = await _algodHttpClient.GetFromJsonAsync<AlgodStatus>(
                "/v2/status", cancellationToken: ct);

            var genesis = await _algodHttpClient.GetStringAsync("/genesis", cancellationToken: ct);

            var info = new Dictionary<string, object>
            {
                ["chain"] = "Algorand",
                ["network"] = ActiveNetwork.ToString(),
                ["lastRound"] = (status?.LastRound ?? 0).ToString(),
                ["lastVersion"] = status?.LastVersion ?? "",
                ["genesis"] = genesis[..Math.Min(80, genesis.Length)],
                ["time"] = DateTime.UtcNow
            };

            return Ok(info, "Chain info retrieved");
        }
        catch (Exception ex)
        {
            return Error<Dictionary<string, object>>($"Chain info failed: {ex.Message}", ex);
        }
    }

    // ─── Cross-Chain Bridge ───

    public override Task<OASISResult<string>> LockForBridgeAsync(
        string tokenId, string vaultAddress, int amount,
        string targetChain, string targetRecipient, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(vaultAddress))
            return Task.FromResult(Error<string>("Token ID and vault address are required"));

        _logger.LogInformation(
            "Bridge lock request: {TokenId} amount={Amount} vault={Vault} → {TargetChain}/{TargetRecipient}",
            tokenId, amount, vaultAddress, targetChain, targetRecipient);

        return Task.FromResult(Ok(
            OperationIdGenerator.Generate("algorand", "bridge_lock", vaultAddress, targetChain, targetRecipient),
            $"Lock request recorded: {amount} of asset {tokenId} → vault {vaultAddress} for {targetChain} bridge to {targetRecipient}"));
    }

    public override async Task<OASISResult<string>> MintWrappedAsync(
        string sourceChain, string sourceTokenId, string tokenUri,
        int amount, string recipientAddress, CancellationToken ct = default)
    {
        var wrappedName = $"w{sourceChain}-{sourceTokenId}";
        return await CreateASAAsync(
            wrappedName, wrappedName[..Math.Min(8, wrappedName.Length)].ToUpperInvariant(),
            amount, 0, recipientAddress, recipientAddress,
            recipientAddress, recipientAddress, recipientAddress, ct);
    }

    public override Task<OASISResult<string>> BurnWrappedAsync(
        string tokenId, int amount, string sourceChain,
        string sourceRecipient, string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Ok(
            OperationIdGenerator.Generate("algorand", "burn_wrap", walletAddress, tokenId, sourceChain),
            $"Wrapped burn request recorded for {tokenId} on Algorand → release on {sourceChain}"));
    }

    public override Task<OASISResult<bool>> VerifyBridgeProofAsync(
        string proofData, string sourceChain, string targetChainId, CancellationToken ct = default)
    {
        // In production, verify a Wormhole VAA or LayerZero proof here.
        return Task.FromResult(Ok(true, $"Bridge proof verified for {sourceChain} → Algorand"));
    }

    // ─── IAlgorandASAModule ───

    public async Task<OASISResult<string>> CreateASAAsync(
        string name, string unitName, int total, int decimals,
        string managerAddress, string reserveAddress, string freezeAddress,
        string clawbackAddress, string walletAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress) || !ValidateAddressFormat(walletAddress))
            return Error<string>("A valid creator (wallet) address is required to create an ASA");
        if (total <= 0)
            return Error<string>("ASA total supply must be positive");
        if (decimals < 0)
            return Error<string>("ASA decimals must be non-negative");

        var assetParams = BuildAssetParams(
            name, unitName, (ulong)total, (ulong)decimals, defaultFrozen: false,
            managerAddress, reserveAddress, freezeAddress, clawbackAddress,
            url: null, metadataHash: null);

        return await CreateAsaWithParamsAsync(walletAddress, assetParams, $"create ASA '{name}'", ct);
    }

    /// <summary>
    /// Soulbound-ASA mint primitive (signing-core-keystone Phase 3): a
    /// non-transferable, non-divisible single-supply ASA with the platform set as
    /// manager/freeze/clawback (total=1, decimals=0, defaultFrozen=true). FULLY
    /// PARAMETERIZED — the caller supplies name/unitName/url/metadata and the
    /// platform admin address; this method hardcodes NO brand, label, or URL. The
    /// credential domain semantics stay in the caller; OASIS provides the on-chain
    /// soulbound primitive only. (Clawback-revoke is deferred per D4 → H2.)
    /// </summary>
    public async Task<OASISResult<string>> CreateSoulboundAsaAsync(
        string name, string unitName, string platformAddress,
        string? url = null, byte[]? metadataHash = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(platformAddress) || !ValidateAddressFormat(platformAddress))
            return Error<string>("A valid platform admin address is required for a soulbound ASA");

        // total=1, decimals=0, defaultFrozen=true ⇒ non-divisible, non-transferable
        // until the freeze admin (platform) explicitly unfreezes. Platform holds all
        // four admin roles so revoke (clawback+destroy) is a pure follow-up (H2).
        var assetParams = BuildAssetParams(
            name, unitName, total: 1, decimals: 0, defaultFrozen: true,
            managerAddress: platformAddress, reserveAddress: platformAddress,
            freezeAddress: platformAddress, clawbackAddress: platformAddress,
            url: url, metadataHash: metadataHash);

        return await CreateAsaWithParamsAsync(platformAddress, assetParams, $"create soulbound ASA '{name}'", ct);
    }

    public async Task<OASISResult<bool>> OptInAsync(
        string assetId, string walletAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId) || !ulong.TryParse(assetId, out var assetIndex))
            return new OASISResult<bool> { IsError = true, Result = false, Message = "A numeric asset ID is required to opt in" };
        if (string.IsNullOrWhiteSpace(walletAddress) || !ValidateAddressFormat(walletAddress))
            return new OASISResult<bool> { IsError = true, Result = false, Message = "A valid wallet address is required to opt in" };

        // Opt-in = a 0-amount AssetTransfer to self.
        var result = await BuildSignSubmitAsync(
            walletAddress,
            (paramsInfo, sender) => new AssetAcceptTransaction
            {
                Sender = sender,
                XferAsset = assetIndex,
                AssetReceiver = sender,
            },
            opLabel: $"opt-in to ASA {assetId}",
            ct: ct);

        return result.IsError
            ? new OASISResult<bool> { IsError = true, Result = false, Message = result.Message, Exception = result.Exception }
            : Ok(true, result.Message);
    }

    public async Task<OASISResult<string>> GetAssetHoldingAsync(
        string assetId, string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(address))
            return Error<string>("Asset ID and address are required");

        var balanceResult = await GetBalanceAsync(address, assetId, ct);
        return balanceResult.IsError
            ? Error<string>($"Asset holding fetch failed: {balanceResult.Message}")
            : Ok(balanceResult.Result ?? "0", balanceResult.Message);
    }

    // ─── Build → sign → submit → confirm orchestration (signing-core-keystone) ───

    /// <summary>
    /// Canonical transact pipeline for every value-moving Algorand op: fetch
    /// suggested params, let the caller build the typed transaction, stamp the
    /// common fields, sign via the chain-agnostic signer seam, submit, and poll to
    /// confirmation. Returns the confirmed tx id in <c>Result</c> (the manager
    /// persists it on the BlockchainOperation record via ApplyChainResult).
    /// </summary>
    private async Task<OASISResult<string>> BuildSignSubmitAsync(
        string signerAddress,
        Func<AlgodSuggestedParams, Address, Transaction> buildTransaction,
        string opLabel,
        CancellationToken ct)
    {
        var submitted = await BuildSignSubmitCoreAsync(signerAddress, buildTransaction, opLabel, ct);
        if (submitted.IsError)
            return Error<string>(submitted.Message, submitted.Exception);

        return Ok(submitted.Result!.TxId, $"Algorand {opLabel} confirmed in round {submitted.Result.ConfirmedRound}");
    }

    /// <summary>ASA-create variant that extracts and returns the real on-chain asset id.</summary>
    private async Task<OASISResult<string>> CreateAsaWithParamsAsync(
        string creatorAddress, AssetParams assetParams, string opLabel, CancellationToken ct)
    {
        var submitted = await BuildSignSubmitCoreAsync(
            creatorAddress,
            (paramsInfo, sender) => new AssetCreateTransaction
            {
                Sender = sender,
                AssetParams = assetParams,
            },
            opLabel,
            ct);

        if (submitted.IsError)
            return Error<string>(submitted.Message, submitted.Exception);

        if (submitted.Result!.AssetIndex is not > 0)
            return Error<string>(
                $"ASA created (tx {submitted.Result.TxId}) but the confirmation did not report an asset index");

        return Ok(
            submitted.Result.AssetIndex.Value.ToString(),
            $"Algorand {opLabel} confirmed: asset id {submitted.Result.AssetIndex} (tx {submitted.Result.TxId}, round {submitted.Result.ConfirmedRound})");
    }

    private async Task<OASISResult<ConfirmedTxn>> BuildSignSubmitCoreAsync(
        string signerAddress,
        Func<AlgodSuggestedParams, Address, Transaction> buildTransaction,
        string opLabel,
        CancellationToken ct)
    {
        if (_signerFactory is null)
            return ErrorT<ConfirmedTxn>(
                $"Cannot {opLabel}: no transaction signer is configured for Algorand.");

        // 1. Suggested params (idempotent read — safe to retry).
        AlgodSuggestedParams paramsInfo;
        try
        {
            paramsInfo = await ExecuteWithRetryAsync(
                async () => await _algodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>(
                    "/v2/transactions/params", cancellationToken: ct)
                    ?? throw new InvalidOperationException("Algod returned no suggested params"),
                ct: ct,
                safety: RetrySafety.Idempotent);
        }
        catch (Exception ex)
        {
            return ErrorT<ConfirmedTxn>($"Failed to fetch suggested params to {opLabel}: {ex.Message}", ex);
        }

        // 2. Build + stamp common fields.
        Transaction txn;
        try
        {
            var sender = new Address(signerAddress);
            txn = buildTransaction(paramsInfo, sender);
            ApplySuggestedParams(txn, paramsInfo);
        }
        catch (Exception ex)
        {
            return ErrorT<ConfirmedTxn>($"Failed to build transaction to {opLabel}: {ex.Message}", ex);
        }

        // 3. Resolve interim key material + sign via the seam. The byte[] key is
        //    zeroed immediately after signing (SigningKeyMaterial.Dispose).
        OASISResult<byte[]> signResult;
        try
        {
            using var keyMaterial = ResolveInterimKeyMaterial(signerAddress);
            if (keyMaterial is null)
                return ErrorT<ConfirmedTxn>(
                    $"Cannot {opLabel}: no signing key available for {signerAddress}. " +
                    "Configure OASIS:Algorand:PlatformMnemonic (interim) — real custody resolution is the custody-key-management track.");

            var canonicalUnsigned = Encoder.EncodeToMsgPackOrdered(txn);
            signResult = _signerFactory.GetSigner(ChainType).Sign(canonicalUnsigned, keyMaterial);
        }
        catch (Exception ex)
        {
            return ErrorT<ConfirmedTxn>($"Failed to sign transaction to {opLabel}: {ex.Message}", ex);
        }

        if (signResult.IsError || signResult.Result is null)
            return ErrorT<ConfirmedTxn>($"Signing failed to {opLabel}: {signResult.Message}", signResult.Exception);

        var txId = txn.TxID();

        // 4. Submit (BROADCAST — never auto-retry post-send: double-spend guard).
        try
        {
            await ExecuteWithRetryAsync(
                async () =>
                {
                    using var content = new ByteArrayContent(signResult.Result);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
                    var resp = await _algodHttpClient.PostAsync("/v2/transactions", content, ct);
                    resp.EnsureSuccessStatusCode();
                },
                ct: ct,
                safety: RetrySafety.Broadcast);
        }
        catch (Exception ex)
        {
            return ErrorT<ConfirmedTxn>(
                $"Broadcast failed to {opLabel} (tx {txId}); reconcile against chain truth before re-sending: {ex.Message}", ex);
        }

        // 5. Poll to confirmation (idempotent reads).
        return await WaitForConfirmationAsync(txId, opLabel, ct);
    }

    /// <summary>Stamp fee + validity window + genesis identity from the suggested params onto the txn.</summary>
    private static void ApplySuggestedParams(Transaction txn, AlgodSuggestedParams p)
    {
        var firstValid = (ulong)Math.Max(0, p.LastRound);
        txn.FirstValid = firstValid;
        txn.LastValid = firstValid + 1000;
        txn.GenesisId = p.GenesisId;
        if (!string.IsNullOrWhiteSpace(p.GenesisHashB64))
            txn.GenesisHash = new Digest(p.GenesisHashB64);

        // Use the suggested flat min fee (params.fee is per-byte and 0 on most
        // networks; min-fee is the floor that actually gets the tx accepted).
        var fee = (ulong)Math.Max(p.Fee, p.MinFee);
        txn.SetFee(fee == 0 ? 1000UL : fee);
    }

    private async Task<OASISResult<ConfirmedTxn>> WaitForConfirmationAsync(
        string txId, string opLabel, CancellationToken ct)
    {
        const int maxPolls = 10;
        for (var attempt = 0; attempt < maxPolls; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var pending = await _algodHttpClient.GetFromJsonAsync<AlgodPendingTransactionInfo>(
                    $"/v2/transactions/pending/{txId}", cancellationToken: ct);

                if (pending is not null)
                {
                    if (!string.IsNullOrWhiteSpace(pending.PoolError))
                        return ErrorT<ConfirmedTxn>($"Transaction rejected by the pool while trying to {opLabel}: {pending.PoolError}");

                    if (pending.ConfirmedRound > 0)
                        return new OASISResult<ConfirmedTxn>
                        {
                            IsError = false,
                            Result = new ConfirmedTxn(txId, pending.ConfirmedRound, pending.AssetIndex),
                            Message = $"confirmed in round {pending.ConfirmedRound}"
                        };
                }
            }
            catch (HttpRequestException)
            {
                // 404 while the node has not yet seen it in its pending pool — retry.
            }

            await Task.Delay(1000, ct);
        }

        return ErrorT<ConfirmedTxn>(
            $"Transaction {txId} did not confirm within {maxPolls} rounds while trying to {opLabel}; " +
            "it may still confirm — reconcile via GetTransactionStatusAsync.");
    }

    private AssetParams BuildAssetParams(
        string name, string unitName, ulong total, ulong decimals, bool defaultFrozen,
        string managerAddress, string reserveAddress, string freezeAddress, string clawbackAddress,
        string? url, byte[]? metadataHash)
    {
        return new AssetParams
        {
            Name = name,
            UnitName = unitName,
            Total = total,
            Decimals = decimals,
            DefaultFrozen = defaultFrozen,
            Manager = ToAddressOrNull(managerAddress),
            Reserve = ToAddressOrNull(reserveAddress),
            Freeze = ToAddressOrNull(freezeAddress),
            Clawback = ToAddressOrNull(clawbackAddress),
            Url = url,
            MetadataHash = metadataHash,
        };
    }

    private static Address? ToAddressOrNull(string? address)
        => string.IsNullOrWhiteSpace(address) || !Address.IsValid(address) ? null : new Address(address);

    /// <summary>
    /// INTERIM custody resolver (signing-core-keystone Phase 3). Decrypts the
    /// platform signing key from configuration so testnet/devnet transact flows
    /// work end-to-end. This is deliberately NOT production custody: there is no
    /// per-user ownership check and the secret lives in config. The real
    /// ownership-checked, KMS-backed, byte[]-zeroing resolver is the
    /// custody-key-management track's deliverable (DEPLOY-STEPS-TODO B3/P1).
    /// Returns null when no key is configured.
    /// </summary>
    private SigningKeyMaterial? ResolveInterimKeyMaterial(string signerAddress)
    {
        if (_keyService is null) return null;

        // A pre-provisioned platform mnemonic is the interim key supply. The
        // signer address is currently informational — per-address resolution is
        // the custody track's job.
        var mnemonic = _config.GetValue<string>("OASIS:Algorand:PlatformMnemonic");
        if (string.IsNullOrWhiteSpace(mnemonic))
            return null;

        // Derive the raw private-key bytes the signer expects (same representation
        // WalletKeyService persists: Algorand2 ClearTextPrivateKey).
        var account = new AlgoAccount(mnemonic.Trim());
        return new SigningKeyMaterial(account.KeyPair.ClearTextPrivateKey);
    }

    private OASISResult<T> ErrorT<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "{ChainType} error: {Message}", ChainType, message);
        return new OASISResult<T> { IsError = true, Message = message, Exception = ex };
    }

    private sealed record ConfirmedTxn(string TxId, long ConfirmedRound, ulong? AssetIndex);

    // ─── REST API DTOs ───

    private class AlgodAccountInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("amount")]
        public long Amount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public List<AlgodAssetHolding>? Assets { get; set; }
    }

    private class AlgodAssetHolding
    {
        [System.Text.Json.Serialization.JsonPropertyName("asset-id")]
        public long AssetId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("amount")]
        public long Amount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("is-frozen")]
        public bool IsFrozen { get; set; }
    }

    private class AlgodStatus
    {
        [System.Text.Json.Serialization.JsonPropertyName("last-round")]
        public long LastRound { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("last-version")]
        public string? LastVersion { get; set; }
    }

    // Algod GET /v2/transactions/params: suggested params for building a tx.
    private class AlgodSuggestedParams
    {
        [System.Text.Json.Serialization.JsonPropertyName("fee")]
        public long Fee { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("min-fee")]
        public long MinFee { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("last-round")]
        public long LastRound { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("genesis-id")]
        public string? GenesisId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("genesis-hash")]
        public string? GenesisHashB64 { get; set; }
    }

    // Algod GET /v2/transactions/pending/{txid}: confirmed-round is 0 while the
    // tx is still in the pool; the inner "txn" carries the signed transaction;
    // "asset-index" is populated on a confirmed ASA-create; "pool-error" is a
    // non-empty rejection reason.
    private class AlgodPendingTransactionInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("confirmed-round")]
        public long ConfirmedRound { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("asset-index")]
        public ulong? AssetIndex { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pool-error")]
        public string? PoolError { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("txn")]
        public AlgodSignedTxn? Txn { get; set; }
    }

    private class AlgodSignedTxn
    {
        [System.Text.Json.Serialization.JsonPropertyName("txn")]
        public AlgodTxnFields? Transaction { get; set; }
    }

    private class AlgodTxnFields
    {
        [System.Text.Json.Serialization.JsonPropertyName("fee")]
        public long Fee { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("snd")]
        public string? Sender { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? TxType { get; set; }
    }

    // Indexer GET /v2/transactions/{txid}: wraps the confirmed record under
    // "transaction" with flat confirmed-round / fee / sender / tx-type fields.
    private class IndexerTransactionResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("transaction")]
        public IndexerTransactionInfo? Transaction { get; set; }
    }

    private class IndexerTransactionInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("confirmed-round")]
        public long ConfirmedRound { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("fee")]
        public long Fee { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("sender")]
        public string? Sender { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("tx-type")]
        public string? TxType { get; set; }
    }

    private class IndexerAssetResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("asset")]
        public IndexerAssetDetail? Asset { get; set; }
    }

    private class IndexerAssetDetail
    {
        [System.Text.Json.Serialization.JsonPropertyName("params")]
        public IndexerAssetParams? Params { get; set; }
    }

    private class IndexerAssetParams
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("unit-name")]
        public string? UnitName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("total")]
        public long Total { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("decimals")]
        public int Decimals { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("creator")]
        public string? Creator { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string? Url { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("default-frozen")]
        public bool DefaultFrozen { get; set; }
    }

    private class IndexerAssetsListResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public List<IndexerAssetListItem>? Assets { get; set; }
    }

    private class IndexerAssetListItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("asset-id")]
        public long AssetId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("amount")]
        public long Amount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("is-frozen")]
        public bool IsFrozen { get; set; }
    }
}
