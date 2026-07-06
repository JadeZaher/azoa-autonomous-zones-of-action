using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using Microsoft.Extensions.DependencyInjection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

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

    // value-path-wiring C1: the audited custody choke point. When present, every
    // value-moving signature routes through it — per-user ops via the IDOR-guarded
    // WithSigningKeyAsync, platform/ASA-admin ops via WithPlatformSigningKeyAsync.
    // The unconditional config-mnemonic load for USER ops is gone; the platform
    // mnemonic is reachable ONLY through the platform door (custody or the fallback
    // platform resolver below).
    //
    // The provider is a singleton but IKeyCustodyService is scoped, so production
    // resolves a fresh custody instance per signing op via _custodyScopeFactory
    // (the AlgorandFaucet precedent). _custodyService is the direct-inject seam for
    // unit tests; _keyService is the platform-only fallback for the signing-core
    // tests (and interim testnet) when no custody is wired at all.
    private readonly IKeyCustodyService? _custodyService;
    private readonly IServiceScopeFactory? _custodyScopeFactory;
    private readonly WalletKeyService? _keyService;

    // Provider-scoped faucet: the Algorand test-ALGO dispenser (idempotency-ledger +
    // Algorand2 SDK build/sign/submit) lives behind IAlgorandFaucet; the provider
    // owns the chain's top-up path and delegates the mechanics here. Optional so the
    // test ctors (no faucet wired) construct cleanly — DispenseFromFaucetAsync then
    // surfaces the same "not configured" error a missing mnemonic would.
    private readonly IAlgorandFaucet? _faucet;

    public override string ChainType => "Algorand";
    public string CapabilityName => "Algorand.ASA";
    public override bool SupportsBridging => true;

    // Algorand exposes a server-side faucet; "configured?" is resolved at dispense
    // time so the caller gets the precise "set Blockchain:Faucet:Algorand:Mnemonic"
    // message rather than a generic "not supported".
    public override bool SupportsFaucet => true;

    public AlgorandProvider(IConfiguration config, ILogger<AlgorandProvider> logger)
        : this(config, logger, signerFactory: null, keyService: null)
    {
    }

    public AlgorandProvider(
        IConfiguration config,
        ILogger<AlgorandProvider> logger,
        ITransactionSignerFactory? signerFactory,
        WalletKeyService? keyService)
        : this(config, logger, signerFactory, keyService, custodyService: null, custodyScopeFactory: null)
    {
    }

    public AlgorandProvider(
        IConfiguration config,
        ILogger<AlgorandProvider> logger,
        ITransactionSignerFactory? signerFactory,
        WalletKeyService? keyService,
        IKeyCustodyService? custodyService,
        IServiceScopeFactory? custodyScopeFactory,
        IAlgorandFaucet? faucet = null)
        : base(config, logger)
    {
        _configManager = new BlockchainConfigurationManager(config);
        _signerFactory = signerFactory;
        _keyService = keyService;
        _custodyService = custodyService;
        _custodyScopeFactory = custodyScopeFactory;
        _faucet = faucet;

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

    // H4 (retired deploy registry): real SHA-512/256 checksum validation via Algorand2's
    // Address.IsValid — replaces the prior length+charset-only regex check.
    private static bool ValidateAddressFormat(string address)
    {
        return !string.IsNullOrWhiteSpace(address) && Address.IsValid(address);
    }

    // ─── Account / Wallet ───

    public override async Task<AZOAResult<string>> GetBalanceAsync(
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

    public override async Task<AZOAResult<bool>> ValidateAddressAsync(
        string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new AZOAResult<bool> { IsError = true, Result = false, Message = "Address is required" };

        if (!ValidateAddressFormat(address))
            return new AZOAResult<bool> { IsError = true, Result = false, Message = "Invalid Algorand address format" };

        try
        {
            var account = await _algodHttpClient.GetFromJsonAsync<AlgodAccountInfo>(
                $"/v2/accounts/{address}", cancellationToken: ct);
            return account != null
                ? Ok(true, "Valid Algorand address — confirmed on network")
                : new AZOAResult<bool> { IsError = true, Result = false, Message = "Address not found on network" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool> { IsError = true, Result = false, Message = $"Validation failed: {ex.Message}" };
        }
    }

    // ─── Faucet (dev / test networks only) ───

    /// <summary>
    /// Provider-scoped Algorand top-up: delegates to the idempotent
    /// <see cref="IAlgorandFaucet"/> dispenser (the one real server-side broadcaster).
    /// A client-supplied <paramref name="idempotencyKey"/> wins; otherwise the faucet
    /// derives a deterministic content key from (chain, recipient, amount). The
    /// mainnet guard is the caller's responsibility (and enforced upstream).
    /// </summary>
    public override async Task<AZOAResult<FaucetDispenseResult>> DispenseFromFaucetAsync(
        string toAddress, decimal amount, string? idempotencyKey = null, CancellationToken ct = default)
    {
        if (_faucet is null || !_faucet.IsConfigured)
            return Error<FaucetDispenseResult>(
                "Algorand faucet is not configured (set Blockchain:Faucet:Algorand:Mnemonic).");

        try
        {
            var txHash = string.IsNullOrWhiteSpace(idempotencyKey)
                ? await _faucet.DispenseAsync(toAddress, amount, ct)
                : await _faucet.DispenseAsync(toAddress, amount, idempotencyKey, ct);

            return Ok(
                new FaucetDispenseResult(txHash, IsClientSide: false,
                    $"Dispensed {amount} test ALGO to {toAddress} on {ActiveNetwork}."),
                "Faucet dispense submitted.");
        }
        catch (Exception ex)
        {
            return Error<FaucetDispenseResult>($"Algorand faucet failed: {ex.Message}", ex);
        }
    }

    // ─── Token / Asset Lifecycle ───

    public override async Task<AZOAResult<string>> MintAsync(
        string tokenUri, ulong amount, string assetType, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress) || amount == 0 || string.IsNullOrWhiteSpace(assetType))
            return Error<string>("Wallet address, positive amount, and asset type are required");

        // ASA-create is a platform/ASA-admin op (the platform is manager/reserve/
        // freeze/clawback of the minted asset). A null context defaults to the
        // platform signer; an explicit user context would be rejected at the
        // signer step (a user wallet cannot be the ASA admin here).
        return await CreateAsaCoreAsync(
            assetType, assetType.ToUpperInvariant()[..Math.Min(8, assetType.Length)],
            amount, 0, walletAddress, walletAddress, walletAddress, walletAddress,
            walletAddress, signingContext ?? SigningContext.Platform, ct);
    }

    public override async Task<AZOAResult<string>> BurnAsync(
        string tokenId, ulong amount, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        // Burn = AssetDestroy (acfg with no params), signed by the asset manager.
        // The full supply must be held by the creator/manager for destroy to
        // succeed; the manager (platform) address is the signer — an ASA-admin op.
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
            signingContext: signingContext ?? SigningContext.Platform,
            ct: ct);
    }

    public override async Task<AZOAResult<string>> TransferAsync(
        string tokenId, string fromAddress, string toAddress, ulong amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || !ulong.TryParse(tokenId, out var assetIndex))
            return Error<string>("A numeric asset ID is required to transfer an ASA");
        if (string.IsNullOrWhiteSpace(fromAddress) || !ValidateAddressFormat(fromAddress))
            return Error<string>("A valid sender address is required");
        if (string.IsNullOrWhiteSpace(toAddress) || !ValidateAddressFormat(toAddress))
            return Error<string>("A valid recipient address is required");
        if (amount == 0)
            return Error<string>("Transfer amount must be positive");

        var receiver = new Address(toAddress);
        return await BuildSignSubmitAsync(
            fromAddress,
            (paramsInfo, sender) => new AssetTransferTransaction
            {
                Sender = sender,
                XferAsset = assetIndex,
                AssetReceiver = receiver,
                AssetAmount = amount,
            },
            opLabel: $"transfer {amount} of ASA {tokenId} to {toAddress}",
            // A transfer FROM a user wallet must sign with that user's key. A null
            // context defaults to the platform signer (the platform moving its own
            // asset, e.g. an allocation mint distributed from the platform reserve).
            signingContext: signingContext ?? SigningContext.Platform,
            ct: ct);
    }

    public override Task<AZOAResult<string>> ExchangeAsync(
        string sourceTokenId, string targetTokenId, string exchangeRate,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Exchange on Algorand requires DEX integration (Tinyman/Pact AMM). Not yet implemented."));
    }

    public override Task<AZOAResult<string>> SwapAsync(
        string tokenIn, string tokenOut, decimal amountIn, decimal minAmountOut,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Swap on Algorand requires DEX integration (Tinyman/Pact AMM). Not yet implemented."));
    }

    // ─── Query / Metadata ───

    public override async Task<AZOAResult<Dictionary<string, object>>> GetTokenMetadataAsync(
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

    public override async Task<AZOAResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
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

    public override async Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
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

    public override Task<AZOAResult<string>> DeployContractAsync(
        byte[] contractCode, string walletAddress,
        Dictionary<string, object>? args = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "AVM contract deployment requires TEAL compilation and client-side signing. Not yet implemented."));
    }

    public override Task<AZOAResult<object>> CallContractAsync(
        string contractAddress, string method, Dictionary<string, object> args,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<object>(
            "AVM contract calls require client-side signing. Not yet implemented."));
    }

    public override async Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(
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

    /// <summary>
    /// Real outbound-bridge lock: a platform-signed ASA transfer of <paramref name="amount"/>
    /// of asset <paramref name="tokenId"/> into the bridge <paramref name="vaultAddress"/>
    /// (custodial-trusted mode). Broadcasts a real transaction and returns the on-chain
    /// tx id; a confirm-timeout returns the tx id with the pending marker (never a fake Ok).
    /// The vault must have opted into the ASA out-of-band. See Providers/Blockchain/AGENTS.md §bridge.
    /// </summary>
    public override async Task<AZOAResult<string>> LockForBridgeAsync(
        string tokenId, string vaultAddress, int amount,
        string targetChain, string targetRecipient, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || !ulong.TryParse(tokenId, out var assetIndex))
            return Error<string>("A numeric asset ID is required to lock an ASA for bridging");
        if (string.IsNullOrWhiteSpace(vaultAddress) || !ValidateAddressFormat(vaultAddress))
            return Error<string>("A valid bridge vault address is required to lock an ASA");
        if (amount <= 0)
            return Error<string>("Bridge lock amount must be positive");

        // Resolve the platform (custodial) signer address — the trusted bridge locks
        // value the platform custodies. A missing platform address fails CLOSED with a
        // clear config error rather than fabricating a lock.
        var platformAddress = ResolvePlatformAddress();
        if (string.IsNullOrWhiteSpace(platformAddress) || !ValidateAddressFormat(platformAddress))
            return Error<string>(
                "Cannot lock for bridge: no valid platform signing address is configured "
                + "(set AZOA:Algorand:PlatformMnemonic / platform custody).");

        var vault = new Address(vaultAddress);
        var lockAmount = checked((ulong)amount);

        _logger.LogInformation(
            "Bridge lock: {Amount} of ASA {TokenId} {From} → vault {Vault} (target {TargetChain}/{TargetRecipient})",
            amount, tokenId, platformAddress, vaultAddress, targetChain, targetRecipient);

        return await BuildSignSubmitAsync(
            platformAddress,
            (paramsInfo, sender) => new AssetTransferTransaction
            {
                Sender = sender,
                XferAsset = assetIndex,
                AssetReceiver = vault,
                AssetAmount = lockAmount,
            },
            opLabel: $"lock {amount} of ASA {tokenId} into bridge vault {vaultAddress} for {targetChain}",
            signingContext: SigningContext.Platform,
            ct: ct);
    }

    public override async Task<AZOAResult<string>> MintWrappedAsync(
        string sourceChain, string sourceTokenId, string tokenUri,
        int amount, string recipientAddress, CancellationToken ct = default)
    {
        var wrappedName = $"w{sourceChain}-{sourceTokenId}";
        return await CreateASAAsync(
            wrappedName, wrappedName[..Math.Min(8, wrappedName.Length)].ToUpperInvariant(),
            amount, 0, recipientAddress, recipientAddress,
            recipientAddress, recipientAddress, recipientAddress, ct);
    }

    /// <summary>
    /// Real wrapped-asset burn (inbound reversal): destroys the platform-managed
    /// wrapped ASA <paramref name="tokenId"/> so the original asset can be released on
    /// <paramref name="sourceChain"/>. The wrapped ASA is minted by
    /// <see cref="MintWrappedAsync"/> with the platform as manager/reserve, so a
    /// platform-signed <see cref="AssetDestroyTransaction"/> is the burn primitive
    /// (mirrors <see cref="BurnAsync"/>). Broadcasts a real tx and returns its id;
    /// a confirm-timeout returns the tx id with the pending marker (never a fake Ok).
    /// The platform must hold the full outstanding supply for destroy to succeed —
    /// otherwise the tx is rejected on-chain and this returns that error (fail-loud).
    /// See Providers/Blockchain/AGENTS.md §bridge.
    /// </summary>
    public override async Task<AZOAResult<string>> BurnWrappedAsync(
        string tokenId, int amount, string sourceChain,
        string sourceRecipient, string walletAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || !ulong.TryParse(tokenId, out var assetIndex))
            return Error<string>("A numeric wrapped-asset ID is required to burn for release");
        if (amount <= 0)
            return Error<string>("Wrapped burn amount must be positive");

        // The wrapped ASA is platform-managed (created via MintWrappedAsync); the
        // manager address is the platform account, derived from the same mnemonic the
        // signer uses so Sender matches the signing key. Missing config fails CLOSED.
        var platformAddress = ResolvePlatformAddress();
        if (string.IsNullOrWhiteSpace(platformAddress) || !ValidateAddressFormat(platformAddress))
            return Error<string>(
                "Cannot burn wrapped asset: no valid platform signing address is configured "
                + "(set AZOA:Algorand:PlatformMnemonic / platform custody).");

        _logger.LogInformation(
            "Bridge burn-wrapped: destroy ASA {TokenId} (amount={Amount}) on Algorand → release on {SourceChain} to {SourceRecipient}",
            tokenId, amount, sourceChain, sourceRecipient);

        return await BuildSignSubmitAsync(
            platformAddress,
            (paramsInfo, sender) => new AssetDestroyTransaction
            {
                Sender = sender,
                AssetIndex = assetIndex,
            },
            opLabel: $"burn wrapped ASA {tokenId} → release on {sourceChain}",
            signingContext: SigningContext.Platform,
            ct: ct);
    }

    // VerifyBridgeProofAsync removed (final-hardening-cutover B2): the always-true
    // provider verifier is gone; VAA verification is the WormholeAdapter /
    // Secp256k1VaaSignatureVerifier path only.

    // ─── IAlgorandASAModule ───

    public async Task<AZOAResult<string>> CreateASAAsync(
        string name, string unitName, int total, int decimals,
        string managerAddress, string reserveAddress, string freezeAddress,
        string clawbackAddress, string walletAddress, CancellationToken ct = default)
    {
        // ASA-create is a platform/ASA-admin op, so it signs with the platform key.
        return await CreateAsaCoreAsync(
            name, unitName, checked((ulong)total), decimals,
            managerAddress, reserveAddress, freezeAddress, clawbackAddress,
            walletAddress, SigningContext.Platform, ct);
    }

    public async Task<AZOAResult<string>> CreateASAAsync(
        string name, string unitName, int total, int decimals,
        string managerAddress, string reserveAddress, string freezeAddress,
        string clawbackAddress, string walletAddress, SigningContext signingContext, CancellationToken ct = default)
    {
        // tenant-consent-delegation AC4b: platform-signed, but the caller-supplied
        // context may carry an acting tenant so the custody seam runs the live
        // consent check before decrypt (a tenant-driven FungibleTokenCreate).
        return await CreateAsaCoreAsync(
            name, unitName, checked((ulong)total), decimals,
            managerAddress, reserveAddress, freezeAddress, clawbackAddress,
            walletAddress, signingContext, ct);
    }

    /// <summary>
    /// Shared ASA-create implementation that carries the resolved
    /// <see cref="SigningContext"/> through to the signer (value-path-wiring C1).
    /// </summary>
    private async Task<AZOAResult<string>> CreateAsaCoreAsync(
        string name, string unitName, ulong total, int decimals,
        string managerAddress, string reserveAddress, string freezeAddress,
        string clawbackAddress, string walletAddress, SigningContext signingContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(walletAddress) || !ValidateAddressFormat(walletAddress))
            return Error<string>("A valid creator (wallet) address is required to create an ASA");
        if (total == 0)
            return Error<string>("ASA total supply must be positive");
        if (decimals < 0)
            return Error<string>("ASA decimals must be non-negative");

        var assetParams = BuildAssetParams(
            name, unitName, total, (ulong)decimals, defaultFrozen: false,
            managerAddress, reserveAddress, freezeAddress, clawbackAddress,
            url: null, metadataHash: null);

        return await CreateAsaWithParamsAsync(walletAddress, assetParams, $"create ASA '{name}'", signingContext, ct);
    }

    /// <summary>
    /// Soulbound-ASA mint primitive (signing-core-keystone Phase 3): a
    /// non-transferable, non-divisible single-supply ASA with the platform set as
    /// manager/freeze/clawback (total=1, decimals=0, defaultFrozen=true). FULLY
    /// PARAMETERIZED — the caller supplies name/unitName/url/metadata and the
    /// platform admin address; this method hardcodes NO brand, label, or URL. The
    /// credential domain semantics stay in the caller; AZOA provides the on-chain
    /// soulbound primitive only. (Clawback-revoke is deferred per D4 → H2.)
    /// </summary>
    public async Task<AZOAResult<string>> CreateSoulboundAsaAsync(
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

        return await CreateAsaWithParamsAsync(
            platformAddress, assetParams, $"create soulbound ASA '{name}'", SigningContext.Platform, ct);
    }

    public async Task<AZOAResult<bool>> OptInAsync(
        string assetId, string walletAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId) || !ulong.TryParse(assetId, out var assetIndex))
            return new AZOAResult<bool> { IsError = true, Result = false, Message = "A numeric asset ID is required to opt in" };
        if (string.IsNullOrWhiteSpace(walletAddress) || !ValidateAddressFormat(walletAddress))
            return new AZOAResult<bool> { IsError = true, Result = false, Message = "A valid wallet address is required to opt in" };

        // Opt-in = a 0-amount AssetTransfer to self. Opt-in of the platform
        // account is an ASA-admin op signed by the platform key.
        var result = await BuildSignSubmitAsync(
            walletAddress,
            (paramsInfo, sender) => new AssetAcceptTransaction
            {
                Sender = sender,
                XferAsset = assetIndex,
                AssetReceiver = sender,
            },
            opLabel: $"opt-in to ASA {assetId}",
            signingContext: SigningContext.Platform,
            ct: ct);

        return result.IsError
            ? new AZOAResult<bool> { IsError = true, Result = false, Message = result.Message, Exception = result.Exception }
            : Ok(true, result.Message);
    }

    public async Task<AZOAResult<string>> GetAssetHoldingAsync(
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
    private async Task<AZOAResult<string>> BuildSignSubmitAsync(
        string signerAddress,
        Func<AlgodSuggestedParams, Address, Transaction> buildTransaction,
        string opLabel,
        SigningContext signingContext,
        CancellationToken ct)
    {
        var submitted = await BuildSignSubmitCoreAsync(signerAddress, buildTransaction, opLabel, signingContext, ct);
        if (submitted.IsError)
            return Error<string>(submitted.Message, submitted.Exception);

        // value-path-wiring M1: a confirm-timeout returns a SUCCESS result whose
        // ConfirmedRound is 0 + PendingConfirmation marker (the tx is broadcast,
        // not failed). Surface the tx id so the manager records Pending + TxHash.
        if (submitted.Result!.PendingConfirmation)
            return Ok(submitted.Result.TxId,
                $"{OperationStatus.PendingConfirmationMarker}: Algorand {opLabel} (tx {submitted.Result.TxId}) " +
                "submitted but not yet confirmed; reconcile against chain truth.");

        return Ok(submitted.Result.TxId, $"Algorand {opLabel} confirmed in round {submitted.Result.ConfirmedRound}");
    }

    /// <summary>ASA-create variant that extracts and returns the real on-chain asset id.</summary>
    private async Task<AZOAResult<string>> CreateAsaWithParamsAsync(
        string creatorAddress, AssetParams assetParams, string opLabel,
        SigningContext signingContext, CancellationToken ct)
    {
        var submitted = await BuildSignSubmitCoreAsync(
            creatorAddress,
            (paramsInfo, sender) => new AssetCreateTransaction
            {
                Sender = sender,
                AssetParams = assetParams,
            },
            opLabel,
            signingContext,
            ct);

        if (submitted.IsError)
            return Error<string>(submitted.Message, submitted.Exception);

        // value-path-wiring M1: a confirm-timeout on an ASA-create has no asset
        // index yet (the tx is broadcast, not confirmed). Return the tx id with the
        // pending marker so the manager records Pending + TxHash for reconciliation,
        // rather than a false error.
        if (submitted.Result!.PendingConfirmation)
            return Ok(submitted.Result.TxId,
                $"{OperationStatus.PendingConfirmationMarker}: Algorand {opLabel} (tx {submitted.Result.TxId}) " +
                "submitted but not yet confirmed; the asset id resolves once it confirms.");

        if (submitted.Result.AssetIndex is not > 0)
            return Error<string>(
                $"ASA created (tx {submitted.Result.TxId}) but the confirmation did not report an asset index");

        return Ok(
            submitted.Result.AssetIndex.Value.ToString(),
            $"Algorand {opLabel} confirmed: asset id {submitted.Result.AssetIndex} (tx {submitted.Result.TxId}, round {submitted.Result.ConfirmedRound})");
    }

    private async Task<AZOAResult<ConfirmedTxn>> BuildSignSubmitCoreAsync(
        string signerAddress,
        Func<AlgodSuggestedParams, Address, Transaction> buildTransaction,
        string opLabel,
        SigningContext signingContext,
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

        // 3. Sign via the custody choke point (value-path-wiring C1). The key bytes
        //    are resolved and zeroed inside the custody resolver; only the signed
        //    envelope leaves. The signer resolution depends on the SigningContext:
        //    user-wallet op → WithSigningKeyAsync (IDOR-guarded); platform/ASA-admin
        //    op → WithPlatformSigningKeyAsync. A user context that cannot be resolved
        //    returns a CLEAR ERROR — it NEVER falls back to the platform key.
        AZOAResult<byte[]> signResult;
        try
        {
            var canonicalUnsigned = Encoder.EncodeToMsgPackOrdered(txn);
            signResult = await SignViaCustodyAsync(canonicalUnsigned, opLabel, signingContext);
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

    private async Task<AZOAResult<ConfirmedTxn>> WaitForConfirmationAsync(
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
                        return new AZOAResult<ConfirmedTxn>
                        {
                            IsError = false,
                            Result = new ConfirmedTxn(txId, pending.ConfirmedRound, pending.AssetIndex, PendingConfirmation: false),
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

        // value-path-wiring M1: the tx WAS broadcast; it simply has not confirmed
        // within the poll window. Returning an error here would record the op
        // Failed WITHOUT a TxHash → a slow-but-valid tx becomes permanently
        // false-Failed and a retry could double-submit. Instead return a SUCCESS
        // result carrying the txId with PendingConfirmation=true so the manager
        // records the op Pending + Parameters["TxHash"]=txId and reconciliation
        // settles it from chain truth.
        return new AZOAResult<ConfirmedTxn>
        {
            IsError = false,
            Result = new ConfirmedTxn(txId, ConfirmedRound: 0, AssetIndex: null, PendingConfirmation: true),
            Message =
                $"Transaction {txId} submitted but not confirmed within {maxPolls} rounds while trying to {opLabel}; " +
                "recorded pending — reconcile via GetTransactionStatusAsync."
        };
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
    /// value-path-wiring C1: resolve the signing key through the audited custody
    /// choke point (<see cref="IKeyCustodyService"/>) and return the signed
    /// envelope. The key bytes are resolved + zeroed INSIDE the custody resolver;
    /// only the signed envelope leaves this method.
    /// <list type="bullet">
    /// <item><b>Per-user op</b> (<c>!IsPlatform</c>): routes through
    /// <see cref="IKeyCustodyService.WithSigningKeyAsync{T}"/>, inheriting its IDOR
    /// guard (the avatar must own the wallet) and platform-wallet-type guard.</item>
    /// <item><b>Platform / ASA-admin op</b> (<c>IsPlatform</c>): routes through
    /// <see cref="IKeyCustodyService.WithPlatformSigningKeyAsync{T}"/>.</item>
    /// </list>
    /// INTERIM SAFETY: a per-user context that does not resolve to a real wallet is
    /// a CLEAR ERROR — it NEVER falls back to the platform key (the silent mis-sign
    /// the review flagged). The unconditional config-mnemonic load for user ops is
    /// gone; the platform mnemonic is now reachable ONLY via the platform door.
    /// </summary>
    private async Task<AZOAResult<byte[]>> SignViaCustodyAsync(
        byte[] canonicalUnsigned, string opLabel, SigningContext ctx)
    {
        // The signing delegate: hand the decrypted key bytes to the chain-agnostic
        // signer. Runs inside the custody resolver's decrypt→sign→zero scope.
        Func<byte[], Task<AZOAResult<byte[]>>> sign = keyBytes =>
        {
            using var material = new SigningKeyMaterial(keyBytes);
            return Task.FromResult(_signerFactory!.GetSigner(ChainType).Sign(canonicalUnsigned, material));
        };

        // Resolve the (scoped) custody service. The provider is a singleton, so a
        // production custody instance is resolved per-op from a fresh scope (the
        // AlgorandFaucet precedent); a directly-injected _custodyService is the
        // unit-test seam.
        if (_custodyService is not null)
            return await SignWithCustodyAsync(_custodyService, opLabel, ctx, sign);

        if (_custodyScopeFactory is not null)
        {
            using var scope = _custodyScopeFactory.CreateScope();
            var custody = scope.ServiceProvider.GetRequiredService<IKeyCustodyService>();
            return await SignWithCustodyAsync(custody, opLabel, ctx, sign);
        }

        // No custody wired (signing-core tests / interim testnet). The platform key
        // is still reachable via the platform door ONLY; a per-user op fails closed
        // (it must never fall back to the platform key — value-path-wiring C1).
        if (!ctx.IsPlatform)
            return UserContextNotResolvable(opLabel);

        return await SignWithFallbackPlatformKeyAsync(opLabel, sign);
    }

    /// <summary>
    /// Route signing through the custody choke point. Per-user ops use the
    /// IDOR-guarded <see cref="IKeyCustodyService.WithSigningKeyAsync{T}"/>;
    /// platform/ASA-admin ops use
    /// <see cref="IKeyCustodyService.WithPlatformSigningKeyAsync{T}"/>. A per-user
    /// context with no resolvable wallet/avatar fails closed — never the platform key.
    /// </summary>
    private static async Task<AZOAResult<byte[]>> SignWithCustodyAsync(
        IKeyCustodyService custody, string opLabel, SigningContext ctx,
        Func<byte[], Task<AZOAResult<byte[]>>> sign)
    {
        // tenant-consent-delegation C1/C2/AC4/AC4b: route BOTH the platform and the
        // per-user resolve through the consent-aware overloads, passing the full
        // SigningContext so the custody seam runs the LIVE grant check before any
        // decrypt. A tenant-driven op (ctx.IsTenantDriven) with no covering grant
        // fails closed — on the platform key (Grant/FungibleTokenCreate) AND the
        // user key (Transfer/Refund) alike. Non-tenant-driven ⇒ no grant required.
        if (ctx.IsPlatform)
            return Flatten(await custody.WithPlatformSigningKeyAsync(true, ctx, sign), opLabel);

        if (!ctx.IsResolvableUserContext)
            return UserContextNotResolvable(opLabel);

        return Flatten(await custody.WithSigningKeyAsync(ctx, sign), opLabel);
    }

    /// <summary>
    /// Platform-only fallback resolver (signing-core / interim testnet) used ONLY
    /// when no <see cref="IKeyCustodyService"/> is wired. Loads the config platform
    /// mnemonic via the platform door equivalent and signs. A per-user op never
    /// reaches here (it fails closed above), so the unconditional config-mnemonic
    /// load the review flagged for user ops is gone.
    /// </summary>
    private async Task<AZOAResult<byte[]>> SignWithFallbackPlatformKeyAsync(
        string opLabel, Func<byte[], Task<AZOAResult<byte[]>>> sign)
    {
        // Fail-closed backstop (security review): the ungated platform-key fallback (no
        // custody service ⇒ no consent gate) must NEVER run in a real deployment. A
        // deployed app always sets ASPNETCORE_ENVIRONMENT explicitly (Production /
        // Staging), so we DENY there — a missing custody service in those environments
        // is a DI regression and silently signing ungated would defeat the whole consent
        // chokepoint. Everywhere else (Development, IntegrationTest, or an UNSET env as
        // in unit tests) the fallback is permitted: those contexts have no real value at
        // stake and exercise the signing path directly. Deny-list, not allow-list, so an
        // unrecognised future test env still works while production stays closed.
        var aspnetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? string.Empty;
        var isProtectedEnv = aspnetEnv.Equals("Production", StringComparison.OrdinalIgnoreCase)
            || aspnetEnv.Equals("Staging", StringComparison.OrdinalIgnoreCase);
        if (isProtectedEnv)
            return new AZOAResult<byte[]>
            {
                IsError = true,
                Message =
                    $"Cannot {opLabel}: platform signing fallback is disabled in {aspnetEnv}; " +
                    "the custody service must be wired."
            };

        if (_keyService is null)
            return new AZOAResult<byte[]>
            {
                IsError = true,
                Message =
                    $"Cannot {opLabel}: no custody service and no platform key supply are wired."
            };

        var mnemonic = _config.GetValue<string>(KeyCustodyService.PlatformMnemonicConfigPath);
        if (string.IsNullOrWhiteSpace(mnemonic))
            return new AZOAResult<byte[]>
            {
                IsError = true,
                Message = $"Cannot {opLabel}: no platform signing key configured."
            };

        // Same representation WalletKeyService persists (Algorand2 ClearTextPrivateKey).
        // Copied into a buffer this method owns so it can be zeroed after signing.
        byte[] key = (byte[])new AlgoAccount(mnemonic.Trim()).KeyPair.ClearTextPrivateKey.Clone();
        try
        {
            return await sign(key);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Derive the platform (custodial) account address from the same platform
    /// mnemonic the signer uses (<c>AZOA:Algorand:PlatformMnemonic</c>), so the
    /// transaction Sender matches the key that signs it. Returns null when no
    /// platform mnemonic is configured — the caller then fails CLOSED with a clear
    /// config error instead of fabricating a bridge lock. Used by the bridge lock,
    /// whose interface signature carries no explicit signer address.
    /// </summary>
    private string? ResolvePlatformAddress()
    {
        var mnemonic = _config.GetValue<string>(KeyCustodyService.PlatformMnemonicConfigPath);
        if (string.IsNullOrWhiteSpace(mnemonic))
            return null;
        try
        {
            return new AlgoAccount(mnemonic.Trim()).Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static AZOAResult<byte[]> UserContextNotResolvable(string opLabel) =>
        new()
        {
            IsError = true,
            Message =
                $"Cannot {opLabel}: a per-user custodial signature was requested but the signing " +
                "context has no resolvable wallet/avatar (or no custody service is wired). Refusing " +
                "to fall back to the platform key (value-path-wiring C1 interim safety)."
        };

    /// <summary>
    /// Flatten the custody resolver's outer <see cref="AZOAResult{T}"/> (which
    /// carries resolve-time errors: wallet not found, IDOR rejection, no platform
    /// key) and the signer's inner result (sign-time errors) into one result.
    /// </summary>
    private static AZOAResult<byte[]> Flatten(AZOAResult<AZOAResult<byte[]>> outer, string opLabel)
    {
        if (outer.IsError || outer.Result is null)
            return new AZOAResult<byte[]>
            {
                IsError = true,
                Message = string.IsNullOrWhiteSpace(outer.Message)
                    ? $"Signer resolution failed to {opLabel}."
                    : outer.Message,
                Exception = outer.Exception
            };
        return outer.Result;
    }

    private AZOAResult<T> ErrorT<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "{ChainType} error: {Message}", ChainType, message);
        return new AZOAResult<T> { IsError = true, Message = message, Exception = ex };
    }

    private sealed record ConfirmedTxn(string TxId, long ConfirmedRound, ulong? AssetIndex, bool PendingConfirmation);

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
