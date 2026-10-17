using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using Solnet.Wallet.Utilities;
using SolanaWallet = Solnet.Wallet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>
/// Solana blockchain provider using direct JSON-RPC calls, with a real SPL
/// build?sign?submit?confirm pipeline. See Providers/Blockchain/Solana/AGENTS.md.
/// </summary>
public partial class SolanaProvider : BaseBlockchainProvider, ISolanaMetaplexModule, ISolanaSPLModule, INftTransactionModule, INftProtocolCostModule, IValueTransferTransactionModule
{
    /// <summary>Raw Solana secret length (32-byte Ed25519 seed ++ 32-byte public key).</summary>
    private const int SolanaSecretKeyLength = 64;

    /// <summary>Ed25519 signature length; also the wire-transaction signature slot size.</summary>
    private const int SolanaSignatureLength = 64;

    /// <summary>Max length of a <c>createAccountWithSeed</c> seed, in bytes.</summary>
    private const int MaxAccountSeedLength = 32;

    /// <summary>Decimals used for platform-created SPL mints — amounts are base units.</summary>
    private const int PlatformMintDecimals = 0;

    /// <summary>Config path of the Solana platform signing mnemonic.</summary>
    public const string PlatformMnemonicConfigPath = "AZOA:Solana:PlatformMnemonic";

    private const string ConfirmationPollAttemptsConfigPath = "AZOA:Solana:ConfirmationPollAttempts";
    private const string ConfirmationPollDelayMsConfigPath = "AZOA:Solana:ConfirmationPollDelayMs";
    private const string RpcRetryMaxAttemptsConfigPath = "AZOA:Solana:RpcRetryMaxAttempts";
    private const string RpcRetryInitialDelayMsConfigPath = "AZOA:Solana:RpcRetryInitialDelayMs";

    private HttpClient _rpcHttpClient = null!;
    private readonly BlockchainConfigurationManager _configManager;
    private readonly HttpMessageHandler? _httpMessageHandler;
    private int _requestId;

    // Signing seam. The provider compiles the canonical Solana MESSAGE and delegates
    // Ed25519 signing to the chain-agnostic signer resolved by ChainType; the signer
    // returns the fully framed wire transaction, which this provider submits as-is.
    private readonly ITransactionSignerFactory? _signerFactory;

    // Custody choke point. _custodyService is the direct-inject unit-test seam;
    // _custodyScopeFactory resolves the scoped production custody per signing op
    // (this provider is cached per network by the factory).
    private readonly IKeyCustodyService? _custodyService;
    private readonly IServiceScopeFactory? _custodyScopeFactory;

    private readonly int _confirmationPollAttempts;
    private readonly int _confirmationPollDelayMs;
    private readonly int _rpcRetryMaxAttempts;
    private readonly int _rpcRetryInitialDelayMs;

    public override string ChainType => "Solana";
    public string CapabilityName => "Solana.Metaplex";

    // Solana devnet/testnet top-up is a CLIENT-side RPC airdrop (requestAirdrop):
    // the frontend performs it. The provider supports the faucet path but only
    // acknowledges — it submits nothing server-side.
    public override bool SupportsFaucet => true;

    /// <summary>Read-only / fail-closed provider: no signer, so no value op can be signed.</summary>
    public SolanaProvider(IConfiguration config, ILogger<SolanaProvider> logger)
        : this(config, logger, signerFactory: null, custodyService: null,
               custodyScopeFactory: null, httpMessageHandler: null)
    {
    }

    /// <summary>
    /// Production constructor: every parameter is a root-resolvable singleton, so
    /// <c>AddTransient&lt;SolanaProvider&gt;()</c> selects this overload and the value
    /// path gets a real signer. Custody is resolved per signing op from the scope
    /// factory (see the full constructor for why it is not injected directly).
    /// </summary>
    public SolanaProvider(
        IConfiguration config,
        ILogger<SolanaProvider> logger,
        ITransactionSignerFactory signerFactory,
        IServiceScopeFactory custodyScopeFactory)
        : this(config, logger, signerFactory, custodyService: null,
               custodyScopeFactory, httpMessageHandler: null)
    {
    }

    /// <summary>
    /// Full seam, deliberately NOT container-constructible. <see cref="IKeyCustodyService"/>
    /// is SCOPED while a factory-bound provider lives for the app's lifetime, so a
    /// container-injected custody instance would be a captive dependency; the required
    /// <paramref name="httpMessageHandler"/> parameter (a type never registered in DI, and
    /// with no default) is what keeps the container from selecting this overload.
    /// </summary>
    public SolanaProvider(
        IConfiguration config,
        ILogger<SolanaProvider> logger,
        ITransactionSignerFactory? signerFactory,
        IKeyCustodyService? custodyService,
        IServiceScopeFactory? custodyScopeFactory,
        HttpMessageHandler? httpMessageHandler,
        AZOA.WebAPI.Interfaces.Stores.IWttPreparationStore? wttPreparationStore = null,
        AZOA.WebAPI.Services.Wormhole.WttStageKeyVault? wttStageKeyVault = null,
        AZOA.WebAPI.Services.Wormhole.WttProofVerifier? wttProofVerifier = null)
        : base(config, logger)
    {
        _configManager = new BlockchainConfigurationManager(config);
        _signerFactory = signerFactory;
        _custodyService = custodyService;
        _custodyScopeFactory = custodyScopeFactory;
        _httpMessageHandler = httpMessageHandler;
        _wttPreparationStore = wttPreparationStore;
        _wttStageKeyVault = wttStageKeyVault;
        _wttProofVerifier = wttProofVerifier;

        _confirmationPollAttempts = config.GetValue<int?>(ConfirmationPollAttemptsConfigPath) ?? 10;
        _confirmationPollDelayMs = config.GetValue<int?>(ConfirmationPollDelayMsConfigPath) ?? 1000;
        _rpcRetryMaxAttempts = config.GetValue<int?>(RpcRetryMaxAttemptsConfigPath) ?? 3;
        _rpcRetryInitialDelayMs = config.GetValue<int?>(RpcRetryInitialDelayMsConfigPath) ?? 1000;
    }

    /// <inheritdoc/>
    protected override void OnInitialize(BlockchainNetworkConfig config, ChainNetwork network)
    {
        _rpcHttpClient = BuildRpcClient(config);
    }

    private HttpClient RpcHttpClient
    {
        get
        {
            EnsureStandaloneInitialized(() =>
            {
                var network = _configManager.GetDefaultNetwork(ChainType);
                return (_configManager.GetNetworkConfig(ChainType, network), network);
            });
            return _rpcHttpClient;
        }
    }

    private HttpClient BuildRpcClient(BlockchainNetworkConfig config)
    {
        var client = _httpMessageHandler is null
            ? new HttpClient()
            : new HttpClient(_httpMessageHandler, disposeHandler: false);

        client.BaseAddress = new Uri(config.NodeUrl);
        client.Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 30000);
        return client;
    }

    // --- Faucet (dev / test networks only) ---

    /// <inheritdoc/>
    public override Task<AZOAResult<FaucetDispenseResult>> DispenseFromFaucetAsync(
        string toAddress, decimal amount, string? idempotencyKey = null, CancellationToken ct = default)
    {
        return Task.FromResult(Ok(
            new FaucetDispenseResult(
                TxHash: null,
                IsClientSide: true,
                "Solana devnet/testnet top-up is performed client-side via RPC airdrop (requestAirdrop)."),
            "Client-side airdrop acknowledged."));
    }

    private async Task<SolanaRpcResponse<T>> RpcCallAsync<T>(string method, object[] parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _requestId);
        var body = new SolanaRpcRequest { JsonRpc = "2.0", Id = id, Method = method, Params = parameters };

        var response = await RpcHttpClient.PostAsJsonAsync("", body, cancellationToken: ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SolanaRpcResponse<T>>(cancellationToken: ct);
        return result ?? new SolanaRpcResponse<T> { Error = new SolanaRpcError { Message = "Empty response" } };
    }

    /// <summary>Idempotent RPC read: safe to retry on transport ambiguity.</summary>
    private Task<SolanaRpcResponse<T>> RpcReadAsync<T>(string method, object[] parameters, CancellationToken ct)
    {
        return ExecuteWithRetryAsync(
            () => RpcCallAsync<T>(method, parameters, ct),
            maxRetries: _rpcRetryMaxAttempts,
            initialDelayMs: _rpcRetryInitialDelayMs,
            ct: ct,
            safety: RetrySafety.Idempotent);
    }

    // --- Account / Wallet ---

    /// <inheritdoc/>
    public override async Task<AZOAResult<string>> GetBalanceAsync(
        string address, string? tokenId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Error<string>("Address is required");

        try
        {
            if (string.IsNullOrWhiteSpace(tokenId))
            {
                var response = await RpcReadAsync<SolanaBalanceResult>("getBalance", new object[] { address }, ct);
                if (response.Error != null)
                    return Error<string>($"Balance failed: {response.Error.Message}");

                var sol = (response.Result?.Value ?? 0) / 1_000_000_000.0;
                return Ok(sol.ToString("F9"), $"Retrieved {sol:F9} SOL for {address}");
            }

            // SPL token balance — every token account this owner holds for the mint.
            var tokenResp = await RpcReadAsync<SolanaValueResult<List<SolanaTokenAccount>>>(
                "getTokenAccountsByOwner",
                new object[] { address, new { mint = tokenId }, new { encoding = "jsonParsed" } }, ct);

            // A failed RPC read is NOT a zero balance. Reporting "0" for an unreachable
            // node would let a caller believe the holding is empty — fail loud instead.
            if (tokenResp.Error != null)
                return Error<string>($"SPL balance failed for mint {tokenId}: {tokenResp.Error.Message}");

            var accounts = tokenResp.Result?.Value;
            if (accounts is null || accounts.Count == 0)
                return Ok("0", $"No SPL token account for mint {tokenId}");

            ulong total = 0;
            foreach (var raw in accounts
                .Select(a => a.Account?.Data?.Parsed?.Info?.TokenAmount?.Amount)
                .Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                if (!ulong.TryParse(raw, out var parsed))
                    return Error<string>($"SPL balance for mint {tokenId} returned an unreadable amount '{raw}'");
                total = checked(total + parsed);
            }

            return Ok(total.ToString(), $"Retrieved {total} base units of SPL token {tokenId} for {address}");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<string>($"Balance fetch failed: {ex.Message}", ex);
        }
    }

    public override async Task<AZOAResult<bool>> ValidateAddressAsync(
        string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new AZOAResult<bool> { IsError = true, Result = false, Message = "Address is required" };

        if (!TryParsePublicKey(address, out _))
            return new AZOAResult<bool> { IsError = true, Result = false, Message = "Invalid Solana address (must be a 32-byte base58 public key)" };

        try
        {
            var response = await RpcReadAsync<SolanaBalanceResult>("getBalance", new object[] { address }, ct);
            if (response.Error != null)
                return new AZOAResult<bool> { IsError = true, Result = false, Message = $"Address not found: {response.Error.Message}" };

            return Ok(true, "Valid Solana address — confirmed on network");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return new AZOAResult<bool> { IsError = true, Result = false, Message = $"Validation failed: {ex.Message}" };
        }
    }

    // --- Token / Asset Lifecycle ---

    /// <inheritdoc/>
    public override async Task<AZOAResult<string>> MintAsync(
        string tokenUri, RailAmount amount, string assetType, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        // D13/D15 edge: an SPL token amount is u64 on chain, so this narrowing is the
        // chain's own ceiling, not an interface artefact. It refuses rather than wraps.
        if (NarrowToChainBaseUnits<string>(amount, "mint an SPL token", out var baseUnits) is { } refusal)
            return refusal;
        if (baseUnits == 0)
            return Error<string>("Mint amount must be positive");
        if (string.IsNullOrWhiteSpace(assetType))
            return Error<string>("Asset type is required");
        if (!TryParsePublicKey(walletAddress, out var mintAuthority))
            return Error<string>("A valid Solana mint-authority address (base58) is required to mint");

        // The mint account is created at an address derived from (authority, seed) so
        // the AUTHORITY's single signature authorizes it — a plain createAccount would
        // need the new mint's own keypair to co-sign, which the one-signature signer
        // seam cannot provide. The seed is fresh per call, so each mint is a distinct
        // asset (the Algorand ASA-create analogue).
        var seed = NewAccountSeed();
        if (!PublicKey.TryCreateWithSeed(mintAuthority, seed, TokenProgram.ProgramIdKey, out var mint) || mint is null)
            return Error<string>("Failed to derive the SPL mint address from the mint authority and seed");

        var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(mintAuthority, mint);
        if (destination is null)
            return Error<string>($"Failed to derive the associated token account of {mintAuthority.Key} for the new mint");

        var rent = await GetMinimumBalanceForRentExemptionAsync(TokenProgram.MintAccountDataSize, ct);
        if (rent.IsError)
            return Error<string>(rent.Message, rent.Exception);

        var instructions = new List<TransactionInstruction>
        {
            SystemProgram.CreateAccountWithSeed(
                fromPublicKey: mintAuthority,
                toPublicKey: mint,
                baseAccount: mintAuthority,
                seed: seed,
                lamports: rent.Result,
                space: (ulong)TokenProgram.MintAccountDataSize,
                owner: TokenProgram.ProgramIdKey),
            // Platform is mint AND freeze authority, mirroring the Algorand ASA model
            // where the platform is manager/freeze/clawback of what it issues.
            TokenProgram.InitializeMint(mint, PlatformMintDecimals, mintAuthority, mintAuthority),
            AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(mintAuthority, mintAuthority, mint),
            TokenProgram.MintTo(mint, destination, baseUnits, mintAuthority),
        };

        var opLabel = $"mint {baseUnits} base units of new SPL token {assetType} (mint {mint.Key})";
        var submitted = await BuildSignSubmitCoreAsync(
            mintAuthority, instructions, opLabel, signingContext ?? SigningContext.Platform, ct);

        if (submitted.IsError || submitted.Result is null)
            return Error<string>(submitted.Message, submitted.Exception);

        var uriNote = string.IsNullOrWhiteSpace(tokenUri)
            ? string.Empty
            : $" Token URI '{tokenUri}' is not written on chain (SPL mints carry no metadata field).";

        // A confirm-timeout is NOT a failure: the tx was broadcast. Surface the real
        // signature under the pending marker so the manager records PendingConfirmation
        // + TxHash and reconciliation settles it from chain truth.
        if (submitted.Result.PendingConfirmation)
            return Ok(submitted.Result.Signature,
                $"{OperationStatus.PendingConfirmationMarker}: Solana {opLabel} (tx {submitted.Result.Signature}) " +
                $"submitted but not yet confirmed; mint {mint.Key} exists only once it confirms.{uriNote}");

        return Ok(mint.Key,
            $"Solana {opLabel} confirmed at slot {submitted.Result.Slot} (tx {submitted.Result.Signature}); " +
            $"{baseUnits} base units credited to {destination.Key}.{uriNote}");
    }

    /// <inheritdoc/>
    public override async Task<AZOAResult<string>> BurnAsync(
        string tokenId, RailAmount amount, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (NarrowToChainBaseUnits<string>(amount, $"burn SPL token {tokenId}", out var baseUnits) is { } refusal)
            return refusal;
        if (!TryParsePublicKey(tokenId, out var mint))
            return Error<string>("A valid SPL mint address (base58) is required to burn");
        if (!TryParsePublicKey(walletAddress, out var owner))
            return Error<string>("A valid Solana holder address (base58) is required to burn");
        if (baseUnits == 0)
            return Error<string>("Burn amount must be positive");

        var tokenAccount = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
        if (tokenAccount is null)
            return Error<string>($"Failed to derive the associated token account of {owner.Key} for mint {mint.Key}");

        // Fail closed BEFORE broadcast when there is no account to burn from — a
        // preflight rejection would cost a fee and produce a murkier error.
        var exists = await AccountExistsAsync(tokenAccount, ct);
        if (exists.IsError)
            return Error<string>(exists.Message, exists.Exception);
        if (!exists.Result)
            return Error<string>(
                $"Cannot burn: {owner.Key} holds no associated token account ({tokenAccount.Key}) for mint {mint.Key}.");

        var opLabel = $"burn {baseUnits} base units of SPL token {mint.Key} from {tokenAccount.Key}";
        return await BuildSignSubmitAsync(
            owner,
            new List<TransactionInstruction> { TokenProgram.Burn(tokenAccount, mint, baseUnits, owner) },
            opLabel,
            signingContext ?? SigningContext.Platform,
            ct);
    }

    /// <inheritdoc/>
    public override async Task<AZOAResult<string>> TransferAsync(
        string tokenId, string fromAddress, string toAddress, RailAmount amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (NarrowToChainBaseUnits<string>(amount, $"transfer SPL token {tokenId}", out var baseUnits) is { } refusal)
            return refusal;
        if (!TryParsePublicKey(tokenId, out var mint))
            return Error<string>("A valid SPL mint address (base58) is required to transfer");
        if (!TryParsePublicKey(fromAddress, out var from))
            return Error<string>("A valid Solana sender address (base58) is required");
        if (!TryParsePublicKey(toAddress, out var to))
            return Error<string>("A valid Solana recipient address (base58) is required");
        if (baseUnits == 0)
            return Error<string>("Transfer amount must be positive");

        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(from, mint);
        var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(to, mint);
        if (source is null || destination is null)
            return Error<string>($"Failed to derive the associated token accounts for mint {mint.Key}");

        var sourceExists = await AccountExistsAsync(source, ct);
        if (sourceExists.IsError)
            return Error<string>(sourceExists.Message, sourceExists.Exception);
        if (!sourceExists.Result)
            return Error<string>(
                $"Cannot transfer: {from.Key} holds no associated token account ({source.Key}) for mint {mint.Key}.");

        // ATA reality: a transfer to an owner with no token account for this mint does
        // NOT create one implicitly — the instruction simply fails. Create it in the
        // same transaction (sender pays the rent) so the value actually lands.
        var destinationExists = await AccountExistsAsync(destination, ct);
        if (destinationExists.IsError)
            return Error<string>(destinationExists.Message, destinationExists.Exception);

        // TransferChecked makes the cluster verify the mint and decimals of BOTH
        // accounts, so a wrong-mint account can never silently receive value.
        var decimals = await GetMintDecimalsAsync(mint, ct);
        if (decimals.IsError)
            return Error<string>(decimals.Message, decimals.Exception);

        var instructions = new List<TransactionInstruction>();
        if (!destinationExists.Result)
            instructions.Add(AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(from, to, mint));
        instructions.Add(TokenProgram.TransferChecked(source, destination, baseUnits, decimals.Result, from, mint));

        var ataNote = destinationExists.Result ? string.Empty : " (recipient token account created in the same transaction)";
        var opLabel = $"transfer {baseUnits} base units of SPL token {mint.Key} to {to.Key}{ataNote}";

        return await BuildSignSubmitAsync(
            from, instructions, opLabel, signingContext ?? SigningContext.Platform, ct);
    }

    public override Task<AZOAResult<string>> ExchangeAsync(
        string sourceTokenId, string targetTokenId, string exchangeRate,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Exchange on Solana requires DEX integration (Jupiter/Raydium AMM). Not yet implemented."));
    }

    public override Task<AZOAResult<string>> SwapAsync(
        string tokenIn, string tokenOut, decimal amountIn, decimal minAmountOut,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Swap on Solana requires DEX integration (Jupiter/Raydium AMM). Not yet implemented."));
    }

    // --- Query / Metadata ---

    /// <inheritdoc/>
    public override async Task<AZOAResult<Dictionary<string, object>>> GetTokenMetadataAsync(
        string tokenId, CancellationToken ct = default)
    {
        PublicKey mint;
        try { mint = new PublicKey(tokenId); }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { return Error<Dictionary<string, object>>("A valid Solana mint address is required."); }
        var response = await RpcReadAsync<SolanaTokenSupplyResult>("getTokenSupply", [mint.Key, new { commitment = "finalized" }], ct);
        if (response.Error is not null)
            return Error<Dictionary<string, object>>($"Token supply is unavailable: {response.Error.Message}");
        var supply = response.Result?.Value;
        if (supply is null || !AZOA.WebAPI.Helpers.UInt64Text.TryParse(supply.Amount, out _)
            || supply.Decimals is not { } decimals || decimals is < 0 or > 255)
            return Error<Dictionary<string, object>>("Token supply is missing or malformed.");
        var result = new Dictionary<string, object>
        {
            ["chain"] = "Solana", ["mintAddress"] = mint.Key, ["totalSupply"] = supply.Amount!,
            ["decimals"] = decimals, ["tokenType"] = "SPL Token", ["fetchedAt"] = DateTimeOffset.UtcNow,
        };
        if (supply.UiAmountString is not null) result["uiAmount"] = supply.UiAmountString;
        var metadataAddress = SolanaNftMetadata.Metadata(mint);
        var account = await ReadNativeAccountAsync(metadataAddress.Key, ct);
        result["metadataPresent"] = account is not null;
        if (account is null) return Ok(result, "Mint supply retrieved; no Metaplex metadata account exists.");
        if (account.Owner != SolanaNftMetadata.Program.Key || account.Executable)
            return Error<Dictionary<string, object>>("Metadata account has the wrong program owner or role.");
        SolanaNativeMetadata.Value metadata;
        try { metadata = SolanaNativeMetadata.Read(account.Data); }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or FormatException or ArgumentException)
        { return Error<Dictionary<string, object>>("Metaplex metadata account is malformed."); }
        if (metadata.Mint != mint.Key)
            return Error<Dictionary<string, object>>("Metadata account describes another mint.");
        result["metadataAddress"] = metadataAddress.Key;
        result["name"] = metadata.Name; result["symbol"] = metadata.Symbol; result["uri"] = metadata.Uri;
        result["updateAuthority"] = metadata.Authority; result["isMutable"] = metadata.Mutable;
        result["sellerFeeBasisPoints"] = metadata.Royalty; result["primarySaleHappened"] = metadata.PrimarySale;
        if (metadata.Creators is not null) result["creators"] = metadata.Creators;
        return Ok(result, "Mint supply and on-chain Metaplex metadata retrieved.");
    }
    public override async Task<AZOAResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
        string ownerAddress, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ownerAddress))
                return Error<List<Dictionary<string, object>>>("Owner address is required");

            var tokenResp = await RpcReadAsync<SolanaValueResult<List<SolanaTokenAccount>>>(
                "getTokenAccountsByOwner",
                new object[] { ownerAddress, new { programId = TokenProgram.ProgramIdKey.Key }, new { encoding = "jsonParsed" } }, ct);

            if (tokenResp.Error != null)
                return Error<List<Dictionary<string, object>>>($"Token fetch failed: {tokenResp.Error.Message}");

            var tokens = new List<Dictionary<string, object>>();
            foreach (var acc in tokenResp.Result?.Value ?? new List<SolanaTokenAccount>())
            {
                var info = acc.Account?.Data?.Parsed?.Info;
                if (info is null)
                    continue;

                tokens.Add(new Dictionary<string, object>
                {
                    ["mintAddress"] = info.Mint ?? string.Empty,
                    ["amount"] = info.TokenAmount?.Amount ?? "0",
                    ["decimals"] = info.TokenAmount?.Decimals ?? 0,
                    ["owner"] = info.Owner ?? string.Empty,
                    ["state"] = info.State ?? string.Empty
                });
            }

            return Ok(tokens, $"Retrieved {tokens.Count} SPL tokens for {ownerAddress}");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<List<Dictionary<string, object>>>($"Token fetch failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public override async Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(txHash))
                return Error<Dictionary<string, object>>("Transaction hash is required");

            var response = await RpcReadAsync<SolanaTransactionResult>("getTransaction",
                new object[] { txHash, new { encoding = "json", commitment = "confirmed" } }, ct);

            if (response.Error != null)
                return Error<Dictionary<string, object>>($"Transaction not found: {response.Error.Message}");

            var tx = response.Result;
            if (tx is null)
                return Error<Dictionary<string, object>>(
                    $"Transaction {txHash} is not recorded on the cluster (not yet confirmed, dropped, or outside the RPC's history window)");

            var status = new Dictionary<string, object>
            {
                ["txHash"] = txHash,
                ["chain"] = "Solana",
                ["slot"] = tx.Slot.ToString(),
                ["blockTime"] = tx.BlockTime?.ToString() ?? "unknown",
                ["success"] = tx.Meta?.Err == null,
                ["fee"] = (tx.Meta?.Fee ?? 0).ToString(),
                ["fetchedAt"] = DateTime.UtcNow
            };

            return Ok(status, "Transaction status retrieved");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<Dictionary<string, object>>($"Status fetch failed: {ex.Message}", ex);
        }
    }



    public override async Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(
        CancellationToken ct = default)
    {
        try
        {
            var slotResp = await RpcReadAsync<long>("getSlot", Array.Empty<object>(), ct);
            var supplyResp = await RpcReadAsync<SolanaSupplyResult>("getSupply", Array.Empty<object>(), ct);

            var info = new Dictionary<string, object>
            {
                ["chain"] = "Solana",
                ["network"] = ActiveNetwork.ToString(),
                ["currentSlot"] = slotResp.Error == null ? slotResp.Result.ToString() : "unknown",
                ["totalSupply"] = supplyResp.Result?.Value?.Total.ToString() ?? "unknown",
                ["circulatingSupply"] = supplyResp.Result?.Value?.Circulating.ToString() ?? "unknown",
                ["rpcEndpoint"] = RpcHttpClient.BaseAddress?.ToString() ?? "",
                ["time"] = DateTime.UtcNow
            };

            return Ok(info, "Chain info retrieved");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<Dictionary<string, object>>($"Chain info failed: {ex.Message}", ex);
        }
    }

    // --- Cross-Chain Bridge ---
    // Solana implements NEITHER IBridgeSourceLeg NOR IBridgeTargetLeg, and that absence
    // is the whole refusal: no reviewed Solana bridge lifecycle exists — no proof that a
    // configured vault is controlled by the signing key, and no wrapped-asset
    // lock/mint/burn/release design for this chain. The bridge orchestrator resolves the
    // legs through TryGetModule and fails closed when they are missing, so there is no
    // refusal body here that could ever regress into a fabricated success.
    //
    // This is NOT a signer problem. SolanaTransactionSigner is genuinely complete; what
    // is missing is the provider-side SPL build?submit pipeline and the reviewed bridge
    // lifecycle on top of it. The plain SPL transfer/mint/burn surface is real and
    // unaffected. See Providers/Blockchain/AGENTS.md §bridge.

    // --- Build ? sign ? submit ? confirm orchestration ---

    /// <summary>
    /// Canonical transact pipeline for a value-moving SPL op that reports its real
    /// transaction signature: fetch a recent blockhash, compile the canonical message,
    /// sign through the custody choke point, broadcast exactly once, and poll to
    /// confirmation.
    /// </summary>
    private async Task<AZOAResult<string>> BuildSignSubmitAsync(
        PublicKey feePayer,
        IReadOnlyList<TransactionInstruction> instructions,
        string opLabel,
        SigningContext signingContext,
        CancellationToken ct)
    {
        var submitted = await BuildSignSubmitCoreAsync(feePayer, instructions, opLabel, signingContext, ct);
        if (submitted.IsError || submitted.Result is null)
            return Error<string>(submitted.Message, submitted.Exception);

        // A confirm-timeout returns a SUCCESS result carrying the real signature with
        // the pending marker: the tx WAS broadcast, so recording it Failed (without a
        // TxHash) would strand a valid transfer and invite a re-broadcast.
        if (submitted.Result.PendingConfirmation)
            return Ok(submitted.Result.Signature,
                $"{OperationStatus.PendingConfirmationMarker}: Solana {opLabel} (tx {submitted.Result.Signature}) " +
                "submitted but not yet confirmed; reconcile against chain truth.");

        return Ok(submitted.Result.Signature,
            $"Solana {opLabel} confirmed at slot {submitted.Result.Slot} (tx {submitted.Result.Signature})");
    }

    private async Task<AZOAResult<ConfirmedSolanaTxn>> BuildSignSubmitCoreAsync(
        PublicKey feePayer,
        IReadOnlyList<TransactionInstruction> instructions,
        string opLabel,
        SigningContext signingContext,
        CancellationToken ct)
    {
        if (_signerFactory is null)
            return Error<ConfirmedSolanaTxn>($"Cannot {opLabel}: no transaction signer is configured for Solana.");
        if (instructions.Count == 0)
            return Error<ConfirmedSolanaTxn>($"Cannot {opLabel}: no instructions were built.");

        // 1. Recent blockhash (idempotent read — safe to retry). The blockhash lives
        //    INSIDE the message the signer signs, so it must be set before compiling.
        string blockhash;
        try
        {
            var blockhashResp = await RpcReadAsync<SolanaValueResult<SolanaBlockhashInfo>>(
                "getLatestBlockhash", new object[] { new { commitment = "finalized" } }, ct);

            if (blockhashResp.Error != null)
                return Error<ConfirmedSolanaTxn>($"Failed to fetch a recent blockhash to {opLabel}: {blockhashResp.Error.Message}");

            blockhash = blockhashResp.Result?.Value?.Blockhash ?? string.Empty;
            if (string.IsNullOrWhiteSpace(blockhash))
                return Error<ConfirmedSolanaTxn>($"Failed to fetch a recent blockhash to {opLabel}: the cluster returned none.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<ConfirmedSolanaTxn>($"Failed to fetch a recent blockhash to {opLabel}: {ex.Message}", ex);
        }

        // 2. Compile the canonical MESSAGE — the exact bytes a Solana validator
        //    hashes. AccountKeys[0] is the fee payer, which the signer verifies
        //    against the key custody resolved (see Solana/AGENTS.md §signer).
        byte[] canonicalMessage;
        try
        {
            var builder = new TransactionBuilder()
                .SetRecentBlockHash(blockhash)
                .SetFeePayer(feePayer);
            foreach (var instruction in instructions)
                builder.AddInstruction(instruction);

            canonicalMessage = builder.CompileMessage();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<ConfirmedSolanaTxn>($"Failed to build the transaction to {opLabel}: {ex.Message}", ex);
        }

        // 3. Sign via the custody choke point. Key bytes are resolved and zeroed
        //    inside the resolver; only the signed wire transaction leaves.
        AZOAResult<byte[]> signResult;
        try
        {
            signResult = await SignViaCustodyAsync(canonicalMessage, opLabel, signingContext);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<ConfirmedSolanaTxn>($"Failed to sign the transaction to {opLabel}: {ex.Message}", ex);
        }

        if (signResult.IsError || signResult.Result is null || signResult.Result.Length == 0)
            return Error<ConfirmedSolanaTxn>($"Signing failed to {opLabel}: {signResult.Message}", signResult.Exception);

        // The signer returns the fully framed wire transaction. Re-serializing it here
        // would invalidate the signature, so these bytes are submitted UNMODIFIED.
        var signedWireTransaction = signResult.Result;

        // 4. The transaction id is READ OUT of the signed bytes — never generated.
        //    Doing it before broadcast means a broadcast failure still names the real
        //    signature to reconcile against.
        var signature = ReadTransactionSignature(signedWireTransaction);
        if (signature.IsError || string.IsNullOrWhiteSpace(signature.Result))
            return Error<ConfirmedSolanaTxn>($"Cannot {opLabel}: {signature.Message}", signature.Exception);
        var txSignature = signature.Result!;

        // 5. Broadcast. RetrySafety.Broadcast retries ONLY provably pre-broadcast
        //    transport failures; every ambiguous post-send error surfaces for
        //    reconciliation instead of being re-sent.
        SolanaRpcResponse<string> submitResponse;
        try
        {
            var encodedTransaction = Convert.ToBase64String(signedWireTransaction);
            submitResponse = await ExecuteWithRetryAsync(
                () => RpcCallAsync<string>(
                    "sendTransaction",
                    new object[] { encodedTransaction, new { encoding = "base64", preflightCommitment = "confirmed" } },
                    ct),
                maxRetries: _rpcRetryMaxAttempts,
                initialDelayMs: _rpcRetryInitialDelayMs,
                ct: ct,
                safety: RetrySafety.Broadcast);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<ConfirmedSolanaTxn>(
                $"Broadcast failed to {opLabel} (tx {txSignature}); reconcile against chain truth before re-sending: {ex.Message}", ex);
        }

        if (submitResponse.Error != null)
            return Error<ConfirmedSolanaTxn>(
                $"The cluster rejected the transaction to {opLabel} (tx {txSignature}): {submitResponse.Error.Message}");

        // A signature the cluster echoes back that differs from the one inside our
        // signed bytes means we do not know what was broadcast. Never record either id.
        if (!string.IsNullOrWhiteSpace(submitResponse.Result)
            && !string.Equals(submitResponse.Result, txSignature, StringComparison.Ordinal))
            return Error<ConfirmedSolanaTxn>(
                $"The cluster returned signature {submitResponse.Result} while trying to {opLabel}, but the signed "
                + $"transaction's signature is {txSignature}. Refusing to record a mismatched id — reconcile both against chain truth.");

        // 6. Poll to confirmation (idempotent reads).
        return await WaitForConfirmationAsync(txSignature, opLabel, ct);
    }

    private async Task<AZOAResult<ConfirmedSolanaTxn>> WaitForConfirmationAsync(
        string signature, string opLabel, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _confirmationPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await RpcCallAsync<SolanaValueResult<List<SolanaSignatureStatus?>>>(
                    "getSignatureStatuses",
                    new object[] { new[] { signature }, new { searchTransactionHistory = true } },
                    ct);

                var status = response.Error is null
                    ? response.Result?.Value?.FirstOrDefault()
                    : null;

                if (status is not null)
                {
                    // The transaction landed and the runtime rejected it. That is a real
                    // on-chain failure with a real signature — report it, do not retry.
                    if (status.Err is not null)
                        return Error<ConfirmedSolanaTxn>(
                            $"Transaction {signature} landed on chain but FAILED while trying to {opLabel}: {status.Err}");

                    if (IsConfirmedCommitment(status.ConfirmationStatus))
                        return Ok(
                            new ConfirmedSolanaTxn(signature, status.Slot, PendingConfirmation: false),
                            $"confirmed at slot {status.Slot}");
                }
            }
            catch (HttpRequestException)
            {
                // Transient RPC hiccup while polling — keep waiting; the broadcast
                // already happened and must not be repeated.
            }

            await Task.Delay(_confirmationPollDelayMs, ct);
        }

        // The tx WAS broadcast; it simply has not confirmed inside the poll window.
        // Returning an error here would record the op Failed WITHOUT a signature, and
        // a slow-but-valid tx would become permanently false-Failed.
        return Ok(
            new ConfirmedSolanaTxn(signature, Slot: 0, PendingConfirmation: true),
            $"Transaction {signature} submitted but not confirmed within {_confirmationPollAttempts} polls while "
            + $"trying to {opLabel}; recorded pending — reconcile via GetTransactionStatusAsync.");
    }

    private static bool IsConfirmedCommitment(string? confirmationStatus) =>
        string.Equals(confirmationStatus, "confirmed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(confirmationStatus, "finalized", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the real transaction id out of the signer's wire bytes.</summary>
    private AZOAResult<string> ReadTransactionSignature(byte[] signedWireTransaction)
    {
        try
        {
            var transaction = Transaction.Deserialize(signedWireTransaction);
            var signature = transaction?.Signatures?.FirstOrDefault()?.Signature;

            if (signature is null || signature.Length != SolanaSignatureLength)
                return Error<string>("the signed Solana transaction carries no usable Ed25519 signature");

            return Ok(Encoders.Base58.EncodeData(signature));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<string>($"the signed Solana transaction could not be decoded to read its signature: {ex.Message}", ex);
        }
    }

    // --- Custody / signing seam ---

    /// <summary>
    /// Resolves the signing key through the audited custody choke point and returns
    /// the signed wire transaction. A per-user context that does not resolve is a
    /// CLEAR ERROR — it never falls back to the platform key.
    /// </summary>
    private async Task<AZOAResult<byte[]>> SignViaCustodyAsync(
        byte[] canonicalMessage, string opLabel, SigningContext ctx)
    {
        Func<byte[], Task<AZOAResult<byte[]>>> sign = keyBytes =>
        {
            // Fail closed on a wrong-shaped key rather than handing it to the signer:
            // the message names the ACTUAL cause, which for a platform op today is that
            // custody returned an incompatible key representation.
            if (keyBytes is null || keyBytes.Length != SolanaSecretKeyLength)
                return Task.FromResult(Error<byte[]>(WrongShapedKeyMessage(opLabel, ctx, keyBytes?.Length ?? 0)));

            using var material = new SigningKeyMaterial(keyBytes);
            return Task.FromResult(_signerFactory!.GetSigner(ChainType).Sign(canonicalMessage, material));
        };

        if (_custodyService is not null)
            return await SignWithCustodyAsync(_custodyService, opLabel, ctx, sign);

        if (_custodyScopeFactory is not null)
        {
            using var scope = _custodyScopeFactory.CreateScope();
            var custody = scope.ServiceProvider.GetRequiredService<IKeyCustodyService>();
            return await SignWithCustodyAsync(custody, opLabel, ctx, sign);
        }

        // No custody wired (unit tests / local tooling / interim devnet). The platform
        // key stays reachable through the platform door ONLY; a per-user op fails closed.
        if (!ctx.IsPlatform)
            return UserContextNotResolvable<byte[]>(opLabel);

        return await SignWithFallbackPlatformKeyAsync(opLabel, sign);
    }

    private async Task<AZOAResult<byte[]>> SignWithCustodyAsync(
        IKeyCustodyService custody, string opLabel, SigningContext ctx,
        Func<byte[], Task<AZOAResult<byte[]>>> sign)
    {
        // The consent-aware overloads run the live grant check BEFORE any decrypt, so a
        // tenant-driven op with no covering grant fails closed on both doors.
        if (ctx.IsPlatform)
            return Flatten(await custody.WithPlatformSigningKeyAsync(true, ctx, sign), opLabel);

        if (!ctx.IsResolvableUserContext)
            return UserContextNotResolvable<byte[]>(opLabel);

        return Flatten(await custody.WithSigningKeyAsync(ctx, sign), opLabel);
    }

    /// <summary>
    /// Platform-only fallback used when no <see cref="IKeyCustodyService"/> is wired.
    /// Denied in Production/Staging: a missing custody service there is a DI regression
    /// and signing without the consent gate must never be the silent outcome.
    /// </summary>
    private async Task<AZOAResult<byte[]>> SignWithFallbackPlatformKeyAsync(
        string opLabel, Func<byte[], Task<AZOAResult<byte[]>>> sign)
    {
        var aspnetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? string.Empty;
        if (aspnetEnv.Equals("Production", StringComparison.OrdinalIgnoreCase)
            || aspnetEnv.Equals("Staging", StringComparison.OrdinalIgnoreCase))
            return Error<byte[]>(
                $"Cannot {opLabel}: the Solana platform signing fallback is disabled in {aspnetEnv}; "
                + "the custody service must be wired.");

        var mnemonic = _config.GetValue<string>(PlatformMnemonicConfigPath);
        if (string.IsNullOrWhiteSpace(mnemonic))
            return Error<byte[]>(
                $"Cannot {opLabel}: no custody service is wired and no Solana platform signing key is "
                + $"configured (set {PlatformMnemonicConfigPath}).");

        byte[] key;
        try
        {
            // Same representation WalletKeyService persists for Solana: Solnet's 64-byte
            // secret. Cloned into a buffer this method owns so it can be zeroed.
            key = (byte[])new SolanaWallet(mnemonic.Trim(), WordList.English).Account.PrivateKey.KeyBytes.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            // Never echo the mnemonic or key bytes.
            return Error<byte[]>($"Cannot {opLabel}: the configured Solana platform mnemonic could not be imported.", ex);
        }

        try
        {
            return await sign(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private string WrongShapedKeyMessage(string opLabel, SigningContext ctx, int actualLength)
    {
        var cause = ctx.IsPlatform
            ? "the configured platform custody key has the wrong Solana representation"
            : "the selected wallet key has the wrong Solana representation";
        return $"Cannot {opLabel}: the resolved signing key is {actualLength} bytes, not the "
            + $"{SolanaSecretKeyLength}-byte Solana secret — {cause}. Refusing to sign with a mismatched key.";
    }

    private AZOAResult<T> UserContextNotResolvable<T>(string opLabel) =>
        Error<T>(
            $"Cannot {opLabel}: a per-user custodial signature was requested but the signing context has no "
            + "resolvable wallet/user (or no custody service is wired). Refusing to fall back to the platform key.");

    /// <summary>Flattens the custody resolver's outer result and the signer's inner result.</summary>
    private AZOAResult<byte[]> Flatten(AZOAResult<AZOAResult<byte[]>> outer, string opLabel)
    {
        if (outer.IsError || outer.Result is null)
            return Error<byte[]>(
                string.IsNullOrWhiteSpace(outer.Message) ? $"Signer resolution failed to {opLabel}." : outer.Message,
                outer.Exception);

        return outer.Result;
    }

    // --- Chain reads used by the pipeline ---

    /// <summary>True when an account exists on the cluster; an unreadable probe is an error, never "missing".</summary>
    private async Task<AZOAResult<bool>> AccountExistsAsync(PublicKey account, CancellationToken ct)
    {
        try
        {
            var response = await RpcReadAsync<SolanaValueResult<SolanaAccountInfo>>(
                "getAccountInfo",
                new object[] { account.Key, new { encoding = "base64", commitment = "confirmed" } },
                ct);

            if (response.Error != null)
                return Error<bool>($"Could not read account {account.Key}: {response.Error.Message}");

            return Ok(response.Result?.Value is not null);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<bool>($"Could not read account {account.Key}: {ex.Message}", ex);
        }
    }

    private async Task<AZOAResult<int>> GetMintDecimalsAsync(PublicKey mint, CancellationToken ct)
    {
        try
        {
            var response = await RpcReadAsync<SolanaTokenSupplyResult>("getTokenSupply", new object[] { mint.Key }, ct);
            if (response.Error != null)
                return Error<int>($"Could not read the decimals of mint {mint.Key}: {response.Error.Message}");

            if (response.Result?.Value?.Decimals is not { } decimals || decimals is < 0 or > 255)
                return Error<int>($"Could not read the decimals of mint {mint.Key}: the cluster returned no supply record.");

            return Ok(decimals);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<int>($"Could not read the decimals of mint {mint.Key}: {ex.Message}", ex);
        }
    }

    private async Task<AZOAResult<ulong>> GetMinimumBalanceForRentExemptionAsync(int dataSize, CancellationToken ct)
    {
        try
        {
            var response = await RpcReadAsync<ulong>(
                "getMinimumBalanceForRentExemption", new object[] { dataSize }, ct);

            if (response.Error != null)
                return Error<ulong>($"Could not read the rent-exempt minimum for a {dataSize}-byte account: {response.Error.Message}");
            if (response.Result == 0)
                return Error<ulong>($"The cluster reported a zero rent-exempt minimum for a {dataSize}-byte account.");

            return Ok(response.Result);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Error<ulong>($"Could not read the rent-exempt minimum for a {dataSize}-byte account: {ex.Message}", ex);
        }
    }

    // --- Address helpers ---

    /// <summary>Parses a base58 Solana address, failing closed on anything that is not a canonical 32-byte key.</summary>
    private static bool TryParsePublicKey(string? address, out PublicKey publicKey) => SolanaAddress.TryParse(address, out publicKey);

    /// <summary>A fresh 32-character seed, so every mint derives a distinct account address.</summary>
    private static string NewAccountSeed() => Guid.NewGuid().ToString("N")[..MaxAccountSeedLength];

    private sealed record ConfirmedSolanaTxn(string Signature, long Slot, bool PendingConfirmation);

    // --- Solana JSON-RPC DTOs ---

    private class SolanaRpcRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public int Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("method")]
        public string Method { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("params")]
        public object[] Params { get; set; } = Array.Empty<object>();
    }

    private class SolanaRpcResponse<T>
    {
        [System.Text.Json.Serialization.JsonPropertyName("jsonrpc")]
        public string? JsonRpc { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public int Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("result")]
        public T? Result { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public SolanaRpcError? Error { get; set; }
    }

    private class SolanaRpcError
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")]
        public int Code { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>The <c>{ context, value }</c> envelope most Solana RPC reads wrap their payload in.</summary>
    private class SolanaValueResult<T>
    {
        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public SolanaContext? Context { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public T? Value { get; set; }
    }

    private class SolanaBalanceResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public long Value { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public SolanaContext? Context { get; set; }
    }

    private class SolanaContext
    {
        [System.Text.Json.Serialization.JsonPropertyName("slot")]
        public long Slot { get; set; }
    }

    private class SolanaBlockhashInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("blockhash")]
        public string? Blockhash { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("lastValidBlockHeight")]
        public long LastValidBlockHeight { get; set; }
    }

    private class SolanaAccountInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("lamports")]
        public long Lamports { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("owner")]
        public string? Owner { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("executable")]
        public bool Executable { get; set; }
    }

    private class SolanaSignatureStatus
    {
        [System.Text.Json.Serialization.JsonPropertyName("slot")]
        public long Slot { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("confirmations")]
        public long? Confirmations { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("err")]
        public object? Err { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("confirmationStatus")]
        public string? ConfirmationStatus { get; set; }
    }

    private class SolanaTokenSupplyResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public SolanaTokenAmountInfo? Value { get; set; }
    }

    private class SolanaTokenAmountInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("amount")]
        public string? Amount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("decimals")]
        public int? Decimals { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("uiAmount")]
        public decimal? UiAmount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("uiAmountString")]
        public string? UiAmountString { get; set; }
    }

    private class SolanaTokenAccount
    {
        [System.Text.Json.Serialization.JsonPropertyName("pubkey")]
        public string? Pubkey { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("account")]
        public SolanaTokenAccountData? Account { get; set; }
    }

    private class SolanaTokenAccountData
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public SolanaTokenParsedData? Data { get; set; }
    }

    private class SolanaTokenParsedData
    {
        [System.Text.Json.Serialization.JsonPropertyName("parsed")]
        public SolanaTokenParsedInfo? Parsed { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("program")]
        public string? Program { get; set; }
    }

    private class SolanaTokenParsedInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        public SolanaTokenInfo? Info { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    private class SolanaTokenInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("mint")]
        public string? Mint { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("owner")]
        public string? Owner { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("state")]
        public string? State { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("tokenAmount")]
        public SolanaTokenAmountInfo? TokenAmount { get; set; }
    }

    private class SolanaTransactionResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("slot")]
        public long Slot { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("blockTime")]
        public long? BlockTime { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("meta")]
        public SolanaTransactionMeta? Meta { get; set; }
    }

    private class SolanaTransactionMeta
    {
        [System.Text.Json.Serialization.JsonPropertyName("err")]
        public object? Err { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("fee")]
        public long Fee { get; set; }
    }

    private class SolanaSupplyResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public SolanaSupplyInfo? Value { get; set; }
    }

    private class SolanaSupplyInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("total")]
        public long Total { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("circulating")]
        public long Circulating { get; set; }
    }
}
