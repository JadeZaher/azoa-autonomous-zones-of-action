using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;
using AZOA.WebAPI.Services.Custody;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>
/// Ethereum provider over direct JSON-RPC with a real EIP-1559 build→sign→submit→confirm
/// pipeline. See <c>Providers/Blockchain/Ethereum/AGENTS.md</c>.
/// </summary>
public partial class EthereumProvider : BaseBlockchainProvider, IEthereumValueModule
{
    /// <summary>Config path of the Ethereum platform signing key (0x-prefixed 32-byte secp256k1 scalar).</summary>
    public const string PlatformPrivateKeyConfigPath = "AZOA:Ethereum:PlatformPrivateKey";

    /// <summary>Config path of the ERC-20 contract <see cref="MintAsync"/> falls back to.</summary>
    public const string PlatformTokenContractConfigPath = "AZOA:Ethereum:PlatformTokenContract";

    private const string ConfirmationPollAttemptsConfigPath = "AZOA:Ethereum:ConfirmationPollAttempts";
    private const string ConfirmationPollDelayMsConfigPath = "AZOA:Ethereum:ConfirmationPollDelayMs";
    private const string RpcRetryMaxAttemptsConfigPath = "AZOA:Ethereum:RpcRetryMaxAttempts";
    private const string RpcRetryInitialDelayMsConfigPath = "AZOA:Ethereum:RpcRetryInitialDelayMs";
    private const string GasLimitBufferPercentConfigPath = "AZOA:Ethereum:GasLimitBufferPercent";
    private const string MaxGasLimitConfigPath = "AZOA:Ethereum:MaxGasLimit";
    private const string BaseFeeMultiplierConfigPath = "AZOA:Ethereum:BaseFeeMultiplier";
    private const string DefaultPriorityFeeWeiConfigPath = "AZOA:Ethereum:DefaultPriorityFeeWei";
    private const string MaxFeePerGasWeiCapConfigPath = "AZOA:Ethereum:MaxFeePerGasWeiCap";

    /// <summary>Metadata key that pins the expected EIP-155 chain id for a configured network.</summary>

    private static readonly Sha3Keccack Keccak = new();

    private HttpClient _rpcHttpClient = null!;
    private readonly BlockchainConfigurationManager _configManager;
    private readonly HttpMessageHandler? _httpMessageHandler;
    private int _requestId;

    // Signing seam. The provider builds the canonical EIP-1559 signing payload and
    // delegates secp256k1 signing to the chain-agnostic signer resolved by ChainType;
    // the signer returns the framed wire transaction, which this provider submits as-is.
    private readonly ITransactionSignerFactory? _signerFactory;

    // Custody choke point. _custodyService is the direct-inject unit-test seam;
    // _custodyScopeFactory resolves the scoped production custody per signing op
    // (this provider is cached per network by the factory).
    private readonly IKeyCustodyService? _custodyService;
    private readonly IServiceScopeFactory? _custodyScopeFactory;

    private readonly SemaphoreSlim _chainIdGate = new(1, 1);
    private BigInteger? _chainId;

    private readonly int _confirmationPollAttempts;
    private readonly int _confirmationPollDelayMs;
    private readonly int _rpcRetryMaxAttempts;
    private readonly int _rpcRetryInitialDelayMs;
    private readonly int _gasLimitBufferPercent;
    private readonly BigInteger _maxGasLimit;
    private readonly int _baseFeeMultiplier;
    private readonly BigInteger _defaultPriorityFeeWei;
    private readonly BigInteger _maxFeePerGasWeiCap;

    public override string ChainType => "Ethereum";

    /// <inheritdoc/>
    public string CapabilityName => "Ethereum.Value";

    /// <inheritdoc/>

    /// <summary>
    /// Ethereum test networks are topped up by external, rate-limited third-party
    /// faucets; there is no server-side dispense this provider can perform.
    /// </summary>
    public override bool SupportsFaucet => false;

    /// <summary>Read-only / fail-closed provider: no signer, so no value op can be signed.</summary>
    public EthereumProvider(IConfiguration config, ILogger<EthereumProvider> logger)
        : this(config, logger, signerFactory: null, custodyService: null,
               custodyScopeFactory: null, httpMessageHandler: null)
    {
    }

    /// <summary>
    /// Production constructor: every parameter is a root-resolvable singleton, so
    /// <c>AddTransient&lt;EthereumProvider&gt;()</c> selects this overload and the value
    /// path gets a real signer. Custody is resolved per signing op from the scope
    /// factory (see the full constructor for why it is not injected directly).
    /// </summary>
    public EthereumProvider(
        IConfiguration config,
        ILogger<EthereumProvider> logger,
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
    public EthereumProvider(
        IConfiguration config,
        ILogger<EthereumProvider> logger,
        ITransactionSignerFactory? signerFactory,
        IKeyCustodyService? custodyService,
        IServiceScopeFactory? custodyScopeFactory,
        HttpMessageHandler? httpMessageHandler,
        AZOA.WebAPI.Interfaces.Stores.IEthereumPreparationStore? preparationStore = null,
        AZOA.WebAPI.Services.Wormhole.WttProofVerifier? wttProofVerifier = null)
        : base(config, logger)
    {
        _configManager = new BlockchainConfigurationManager(config);
        _signerFactory = signerFactory;
        _custodyService = custodyService;
        _custodyScopeFactory = custodyScopeFactory;
        _wttProofVerifier = wttProofVerifier;
        _httpMessageHandler = httpMessageHandler;
        _preparationStore = preparationStore;

        _confirmationPollAttempts = config.GetValue<int?>(ConfirmationPollAttemptsConfigPath) ?? 20;
        _confirmationPollDelayMs = config.GetValue<int?>(ConfirmationPollDelayMsConfigPath) ?? 1500;
        _rpcRetryMaxAttempts = config.GetValue<int?>(RpcRetryMaxAttemptsConfigPath) ?? 3;
        _rpcRetryInitialDelayMs = config.GetValue<int?>(RpcRetryInitialDelayMsConfigPath) ?? 1000;
        _gasLimitBufferPercent = config.GetValue<int?>(GasLimitBufferPercentConfigPath) ?? 125;
        _maxGasLimit = ReadBigInteger(config, MaxGasLimitConfigPath, 15_000_000);
        _baseFeeMultiplier = config.GetValue<int?>(BaseFeeMultiplierConfigPath) ?? 2;
        _defaultPriorityFeeWei = ReadBigInteger(config, DefaultPriorityFeeWeiConfigPath, 1_500_000_000);
        _maxFeePerGasWeiCap = ReadBigInteger(config, MaxFeePerGasWeiCapConfigPath, 1_000_000_000_000);
    }

    private static BigInteger ReadBigInteger(IConfiguration config, string path, long fallback)
    {
        var raw = config.GetValue<string?>(path);
        return !string.IsNullOrWhiteSpace(raw)
            && BigInteger.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : new BigInteger(fallback);
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

        // Managed node providers (Alchemy/Infura) accept the project key either in the
        // URL or as a bearer token; honour the configured token when present.
        if (!string.IsNullOrWhiteSpace(config.ApiToken))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiToken);

        return client;
    }

    // ─── JSON-RPC transport ───

    private async Task<EthRpcResult> RpcCallAsync(string method, object?[] parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        var body = new EthRpcRequest { Id = id, Method = method, Params = parameters };

        var response = await RpcHttpClient.PostAsJsonAsync("", body, cancellationToken: ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(ct);
        return EthRpcResult.Parse(payload);
    }

    /// <summary>Idempotent RPC read: safe to retry on transport ambiguity.</summary>
    private Task<EthRpcResult> RpcReadAsync(string method, object?[] parameters, CancellationToken ct)
    {
        return ExecuteWithRetryAsync(
            () => RpcCallAsync(method, parameters, ct),
            maxRetries: _rpcRetryMaxAttempts,
            initialDelayMs: _rpcRetryInitialDelayMs,
            ct: ct,
            safety: RetrySafety.Idempotent);
    }

    private async Task<AZOAResult<BigInteger>> ReadQuantityAsync(
        string method, object?[] parameters, string what, CancellationToken ct)
    {
        try
        {
            var response = await RpcReadAsync(method, parameters, ct);
            if (response.IsError)
                return Error<BigInteger>($"Could not read {what}: {response.ErrorMessage}");
            if (!response.TryGetString(out var hex) || !EthereumHex.TryParseQuantity(hex, out var value))
                return Error<BigInteger>($"Could not read {what}: the node returned '{response.RawText}', not a hex quantity.");

            return Ok(value);
        }
        catch (Exception ex)
        {
            return Error<BigInteger>($"Could not read {what}: {ex.Message}", ex);
        }
    }

    // ─── Chain identity ───

    /// <summary>
    /// The node's EIP-155 chain id, cached for the provider's (single) network binding.
    /// When the configured network pins a <c>ChainId</c> in its metadata, a mismatch is
    /// FATAL: a repointed node URL is how a transaction meant for a test network gets
    /// signed for mainnet, and the chain id is inside the bytes the signer signs.
    /// </summary>
    private async Task<AZOAResult<BigInteger>> GetChainIdAsync(CancellationToken ct)
    {
        var expected = ConfiguredChainId();
        if (expected is null)
            return Error<BigInteger>("An explicit positive per-network Ethereum ChainId is required; legacy metadata is not an identity pin.");
        if (_chainId is { } cached)
            return cached == expected ? Ok(cached) : Error<BigInteger>("Ethereum network identity changed after provider initialization.");

        await _chainIdGate.WaitAsync(ct);
        try
        {
            if (_chainId is { } raced)
                return raced == expected ? Ok(raced) : Error<BigInteger>("Ethereum network identity changed after provider initialization.");

            var read = await ReadQuantityAsync("eth_chainId", Array.Empty<object?>(), "the node's chain id", ct);
            if (read.IsError)
                return read;

            if (read.Result.IsZero)
                return Error<BigInteger>("The node reported chain id 0, which is not a valid EIP-155 chain.");

            if (expected is { } pinned && pinned != read.Result)
                return Error<BigInteger>(
                    $"Chain id mismatch: the configured {ActiveNetwork} network pins chain id {pinned} but the node at "
                    + $"{RpcHttpClient.BaseAddress} reports {read.Result}. Refusing to sign for a chain the configuration "
                    + "does not expect.");

            _chainId = read.Result;
            return Ok(read.Result);
        }
        finally
        {
            _chainIdGate.Release();
        }
    }

    private BigInteger? ConfiguredChainId()
    {
        var chain = _configManager.GetAvailableChains()
            .FirstOrDefault(c => c.ChainType.Equals(ChainType, StringComparison.OrdinalIgnoreCase));

        var raw = ActiveNetwork switch
        {
            ChainNetwork.Devnet => chain?.Devnet.ChainId,
            ChainNetwork.Testnet => chain?.Testnet.ChainId,
            ChainNetwork.Mainnet => chain?.Mainnet.ChainId,
            _ => null,
        };
        return BigInteger.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0 && parsed <= EthereumHex.MaxUInt256 ? parsed
            : null;
    }

    // ─── Account / Wallet ───

    /// <summary>
    /// Native ETH balance in WEI, or — when <paramref name="tokenId"/> is an ERC-20
    /// contract address — the owner's holding in that token's BASE UNITS (the same unit
    /// <see cref="TransferAsync"/> / <see cref="BurnAsync"/> take). Always exact: the
    /// value is carried as <see cref="BigInteger"/> end to end and rendered as a decimal
    /// string, so an arbitrarily large balance is reported whole, never truncated.
    /// </summary>
    public override async Task<AZOAResult<string>> GetBalanceAsync(
        string address, string? tokenId = null, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(address, out var owner))
            return Error<string>("A valid Ethereum address (0x + 40 hex, EIP-55 checksum when mixed case) is required");

        if (EthereumAddress.IsNativeToken(tokenId))
        {
            var native = await GetNativeBalanceWeiAsync(owner, ct);
            if (native.IsError)
                return Error<string>(native.Message, native.Exception);

            return Ok(native.Result.ToString(CultureInfo.InvariantCulture),
                $"Retrieved {native.Result} wei for {owner}");
        }

        if (!EthereumAddress.TryParse(tokenId, out var contract))
            return Error<string>($"'{tokenId}' is neither the native-ETH sentinel nor a valid ERC-20 contract address");

        var erc20 = await GetErc20BalanceAsync(contract, owner, ct);
        if (erc20.IsError)
            return Error<string>(erc20.Message, erc20.Exception);

        return Ok(erc20.Result.ToString(CultureInfo.InvariantCulture),
            $"Retrieved {erc20.Result} base units of ERC-20 {contract} for {owner}");
    }

    /// <summary>
    /// EIP-55-aware, OFFLINE validation. Unlike an account-model chain with rent or
    /// opt-in, every well-formed Ethereum address is spendable-to whether or not it has
    /// ever been seen on chain, so a network probe would add no information — while a
    /// FAILED checksum genuinely means a mistyped address and is rejected here.
    /// </summary>
    public override Task<AZOAResult<bool>> ValidateAddressAsync(
        string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Task.FromResult(new AZOAResult<bool> { IsError = true, Result = false, Message = "Address is required" });

        if (!EthereumAddress.TryParse(address, out var checksummed))
            return Task.FromResult(new AZOAResult<bool>
            {
                IsError = true,
                Result = false,
                Message = "Invalid Ethereum address: expected 0x followed by 40 hex characters, and a mixed-case "
                    + "address must satisfy its EIP-55 checksum.",
            });

        return Task.FromResult(Ok(true, $"Valid Ethereum address (EIP-55 form {checksummed})"));
    }

    /// <summary>
    /// FAIL-CLOSED: Ethereum test-network top-up is performed by external third-party
    /// faucets (rate-limited, captcha-gated, per-network). There is no server-side
    /// dispense this provider could perform, and no configured faucet account.
    /// </summary>
    public override Task<AZOAResult<FaucetDispenseResult>> DispenseFromFaucetAsync(
        string toAddress, decimal amount, string? idempotencyKey = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<FaucetDispenseResult>(
            "Ethereum top-up is not supported: test-network ETH comes from external third-party faucets "
            + "(rate-limited and captcha-gated per network), and no server-side faucet account is provisioned "
            + "for this provider. Fund the address out of band."));
    }

    // ─── Token / Asset Lifecycle (IBlockchainProvider edges) ───

    /// <summary>
    /// Mints <paramref name="amount"/> BASE UNITS of an existing ERC-20 into
    /// <paramref name="walletAddress"/> by calling <c>mint(address,uint256)</c>.
    /// <para>Ethereum has no chain-native asset-creation primitive (unlike an Algorand
    /// ASA or an SPL mint), so the token must already be deployed. The contract is taken
    /// from <paramref name="tokenUri"/> when that is an Ethereum address, else from
    /// <c>AZOA:Ethereum:PlatformTokenContract</c>; with neither, this fails closed.</para>
    /// </summary>
    public override async Task<AZOAResult<string>> MintAsync(
        string tokenUri, RailAmount amount, string assetType, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (RejectUnsettleableAmount<string>(amount, "mint an ERC-20") is { } refusal)
            return refusal;

        var contract = ResolveMintContract(tokenUri);
        if (contract is null)
            return Error<string>(
                "Cannot mint on Ethereum: there is no chain-native asset-creation primitive, so a mint needs an "
                + "already-deployed ERC-20 that exposes mint(address,uint256). Supply its address as the tokenUri "
                + $"argument or configure '{PlatformTokenContractConfigPath}'. Refusing to invent a token id for an "
                + "asset that does not exist.");

        if (!EthereumAddress.TryParse(walletAddress, out var recipient))
            return Error<string>("A valid Ethereum recipient address is required to mint");

        // D13/D15: the interface amount already IS BigInteger base units, so nothing is
        // converted here — IEthereumValueModule is the direct path, not a narrowed edge.
        var baseUnits = amount.BaseUnits;

        // The signer must hold MINTER authority, so the minter IS the platform/user
        // account behind the signing context, not the recipient. Ethereum's interface
        // carries no separate minter argument: the platform token contract's minter is
        // the platform account, and a per-user context mints from that user's account.
        var minter = await ResolveSignerAddressAsync(recipient, signingContext, ct);
        if (minter.IsError)
            return Error<string>(minter.Message, minter.Exception);

        var assetNote = string.IsNullOrWhiteSpace(assetType) ? string.Empty : $" (asset type '{assetType}')";
        var minted = await MintErc20Async(contract, minter.Result!, recipient, baseUnits, signingContext, ct);
        return minted.IsError
            ? minted
            : Ok(minted.Result!, minted.Message + assetNote);
    }

    /// <summary>
    /// Burns <paramref name="amount"/> BASE UNITS of ERC-20 <paramref name="tokenId"/>
    /// held by <paramref name="walletAddress"/> via <c>burn(uint256)</c>.
    /// </summary>
    public override Task<AZOAResult<string>> BurnAsync(
        string tokenId, RailAmount amount, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (RejectUnsettleableAmount<string>(amount, "burn an ERC-20") is { } refusal)
            return Task.FromResult(refusal);
        if (!EthereumAddress.TryParse(tokenId, out var contract))
            return Task.FromResult(Error<string>(
                "A valid ERC-20 contract address is required to burn on Ethereum (native ETH cannot be burned)."));
        if (!EthereumAddress.TryParse(walletAddress, out var holder))
            return Task.FromResult(Error<string>("A valid Ethereum holder address is required to burn"));

        return BurnErc20Async(contract, holder, amount.BaseUnits, signingContext, ct);
    }

    /// <summary>
    /// Transfers <paramref name="amount"/> — WEI when <paramref name="tokenId"/> denotes
    /// native ETH, otherwise the ERC-20's BASE UNITS — from <paramref name="fromAddress"/>
    /// to <paramref name="toAddress"/>. The sender is the fee payer and the signer.
    /// </summary>
    public override Task<AZOAResult<string>> TransferAsync(
        string tokenId, string fromAddress, string toAddress, RailAmount amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (RejectUnsettleableAmount<string>(amount, "transfer on Ethereum") is { } refusal)
            return Task.FromResult(refusal);
        if (!EthereumAddress.TryParse(fromAddress, out var from))
            return Task.FromResult(Error<string>("A valid Ethereum sender address is required"));
        if (!EthereumAddress.TryParse(toAddress, out var to))
            return Task.FromResult(Error<string>("A valid Ethereum recipient address is required"));

        var baseUnits = amount.BaseUnits;

        if (EthereumAddress.IsNativeToken(tokenId))
            return TransferNativeAsync(from, to, baseUnits, signingContext, ct);

        if (!EthereumAddress.TryParse(tokenId, out var contract))
            return Task.FromResult(Error<string>(
                $"'{tokenId}' is neither the native-ETH sentinel nor a valid ERC-20 contract address"));

        return TransferErc20Async(contract, from, to, baseUnits, signingContext, ct);
    }

    public override Task<AZOAResult<string>> ExchangeAsync(
        string sourceTokenId, string targetTokenId, string exchangeRate,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Exchange on Ethereum requires a DEX router integration (Uniswap/0x): the provider builds no swap "
            + "calldata, has no reviewed slippage or deadline policy, and no router allowlist. Refusing to record "
            + "an exchange that did not happen."));
    }

    public override Task<AZOAResult<string>> SwapAsync(
        string tokenIn, string tokenOut, decimal amountIn, decimal minAmountOut,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>(
            "Swap on Ethereum requires a DEX router integration (Uniswap/0x): the provider builds no swap calldata, "
            + "has no reviewed slippage or deadline policy, and no router allowlist. Refusing to record a swap that "
            + "did not happen."));
    }

    // ─── Query / Metadata ───

    /// <summary>Reads the ERC-20 metadata a contract will actually answer for; never invents a field.</summary>
    public override async Task<AZOAResult<Dictionary<string, object>>> GetTokenMetadataAsync(
        string tokenId, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(tokenId, out var contract))
            return Error<Dictionary<string, object>>("A valid ERC-20 contract address is required");

        var hasCode = await ContractHasCodeAsync(contract, ct);
        if (hasCode.IsError)
            return Error<Dictionary<string, object>>(hasCode.Message, hasCode.Exception);
        if (!hasCode.Result)
            return Error<Dictionary<string, object>>(
                $"{contract} holds no code on {ActiveNetwork}: it is an externally owned account, not a token contract.");

        var metadata = new Dictionary<string, object>
        {
            ["chain"] = ChainType,
            ["network"] = ActiveNetwork.ToString(),
            ["contractAddress"] = contract,
            ["tokenType"] = "ERC-20",
            ["fetchedAt"] = DateTime.UtcNow,
        };

        var name = await CallReadAsync(contract, EthereumAbi.EncodeCall(EthereumAbi.NameSignature), null, ct);
        if (!name.IsError && EthereumAbi.TryDecodeText(name.Result, out var nameText))
            metadata["name"] = nameText;

        var symbol = await CallReadAsync(contract, EthereumAbi.EncodeCall(EthereumAbi.SymbolSignature), null, ct);
        if (!symbol.IsError && EthereumAbi.TryDecodeText(symbol.Result, out var symbolText))
            metadata["symbol"] = symbolText;

        var decimals = await CallReadAsync(contract, EthereumAbi.EncodeCall(EthereumAbi.DecimalsSignature), null, ct);
        if (!decimals.IsError && EthereumAbi.TryDecodeUInt8(decimals.Result, out var decimalPlaces))
            metadata["decimals"] = decimalPlaces;

        var supply = await CallReadAsync(contract, EthereumAbi.EncodeCall(EthereumAbi.TotalSupplySignature), null, ct);
        if (!supply.IsError && EthereumAbi.TryDecodeUInt256(supply.Result, out var totalSupply))
            metadata["totalSupply"] = totalSupply.ToString(CultureInfo.InvariantCulture);

        // A contract with code but no readable ERC-20 surface is reported as such rather
        // than returned as a token with every field silently missing.
        if (!metadata.ContainsKey("name") && !metadata.ContainsKey("symbol") && !metadata.ContainsKey("totalSupply"))
            return Error<Dictionary<string, object>>(
                $"{contract} has code but answered none of name()/symbol()/totalSupply(); it does not expose an "
                + "ERC-20 metadata surface.");

        return Ok(metadata, $"ERC-20 metadata retrieved for {contract}");
    }

    /// <summary>
    /// FAIL-CLOSED: Ethereum exposes no chain-native enumeration of an account's token
    /// holdings. The list can only come from an indexer — a full log scan over
    /// <c>Transfer</c> topics, or a third-party token API — and no such integration is
    /// reviewed, configured, or trusted here.
    /// </summary>
    public override Task<AZOAResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
        string ownerAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<List<Dictionary<string, object>>>(
            "Ethereum cannot enumerate an account's token holdings from the node: there is no chain-native index "
            + "from owner to token, so the list requires a Transfer-log scan or a third-party token API, neither of "
            + "which is wired or reviewed here. Refusing to return an empty or partial list a caller could mistake "
            + $"for the truth — query a known contract with GetBalanceAsync({ownerAddress}, <contract>) instead."));
    }

    /// <summary>
    /// Transaction status from the receipt, with the mempool consulted to separate
    /// in-flight from unknown.
    /// <para>The vacuous-null trap is closed explicitly: a missing receipt is NOT a
    /// success, and a receipt whose <c>status</c> field is absent or unparseable is an
    /// ERROR (⇒ <see cref="ChainConfirmation.Unknown"/>), never a confirmation. Only an
    /// explicit <c>0x1</c> reports success.</para>
    /// </summary>
    public override async Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash, CancellationToken ct = default)
    {
        var probe = await ProbeTransactionAsync(txHash, ct);
        if (probe.IsError || probe.Result is null)
            return Error<Dictionary<string, object>>(probe.Message, probe.Exception);

        var observation = probe.Result;
        var status = new Dictionary<string, object>
        {
            ["txHash"] = observation.TxHash,
            ["chain"] = ChainType,
            ["network"] = ActiveNetwork.ToString(),
            ["fetchedAt"] = DateTime.UtcNow,
        };

        switch (observation.Verdict)
        {
            case ChainConfirmation.Confirmed:
            case ChainConfirmation.FailedOnChain:
                // ChainTxClassifier reads "success": true ⇒ Confirmed, false ⇒ FailedOnChain.
                status["success"] = observation.Verdict == ChainConfirmation.Confirmed;
                status["blockNumber"] = observation.BlockNumber.ToString(CultureInfo.InvariantCulture);
                status["gasUsed"] = observation.GasUsed.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(observation.ContractAddress))
                    status["contractAddress"] = observation.ContractAddress;
                return Ok(status, $"Transaction {observation.TxHash} is on chain in block {observation.BlockNumber}");

            case ChainConfirmation.Pending:
                // No "success" key: the classifier maps confirmed=false to Pending, which
                // is "observed in flight — wait", not a failure and not a confirmation.
                status["confirmed"] = false;
                status["pending"] = true;
                return Ok(status, $"Transaction {observation.TxHash} is in the mempool and not yet mined");

            default:
                // Defensive: the probe never returns Unknown as a success result.
                return Error<Dictionary<string, object>>(
                    $"Transaction {observation.TxHash} is not observable on {ActiveNetwork}: {observation.Detail}");
        }
    }

    /// <summary>
    /// Sharpened tri-state (overriding the base classifier round trip): the mempool
    /// lookup positively distinguishes an in-flight transaction (<c>Pending</c>) from one
    /// the node has never seen (<c>Unknown</c>), which is what keeps the reconcile path
    /// from re-broadcasting.
    /// </summary>
    public override async Task<AZOAResult<ChainConfirmation>> GetTransactionConfirmationAsync(
        string txHash, CancellationToken ct = default)
    {
        try
        {
            var probe = await ProbeTransactionAsync(txHash, ct);

            // An errored/absent probe folds to Unknown ("park, do not act") — never a
            // failure verdict, so a flaky RPC can never trigger a re-broadcast.
            return new AZOAResult<ChainConfirmation>
            {
                Result = probe.IsError || probe.Result is null ? ChainConfirmation.Unknown : probe.Result.Verdict,
                Message = probe.Result?.Detail ?? probe.Message,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new AZOAResult<ChainConfirmation> { Result = ChainConfirmation.Unknown, Message = ex.Message };
        }
    }

    /// <summary>Single source of truth for "what does the chain say about this hash".</summary>
    private async Task<AZOAResult<EthereumTxObservation>> ProbeTransactionAsync(string txHash, CancellationToken ct)
    {
        if (!EthereumHex.TryParseData(txHash, out var hashBytes) || hashBytes.Length != 32)
            return Error<EthereumTxObservation>("A 32-byte 0x-prefixed transaction hash is required");

        var normalized = EthereumHex.Data(hashBytes);

        try
        {
            var receiptResponse = await RpcReadAsync("eth_getTransactionReceipt", new object?[] { normalized }, ct);
            if (receiptResponse.IsError)
                return Error<EthereumTxObservation>(
                    $"The receipt lookup for {normalized} failed: {receiptResponse.ErrorMessage}");

            if (!receiptResponse.IsNullResult && receiptResponse.Value.ValueKind == JsonValueKind.Object)
            {
                var receipt = receiptResponse.Value;

                // A receipt without a block number is not mined; treat it as no receipt.
                var mined = TryReadQuantityProperty(receipt, "blockNumber", out var blockNumber);

                if (mined)
                {
                    // THE VACUOUS-NULL TRAP: `status` absent (pre-Byzantium node or a
                    // trimmed proxy response) means we CANNOT tell success from revert.
                    // Reporting success here would settle a reverted transfer.
                    if (!TryReadQuantityProperty(receipt, "status", out var statusFlag) || (statusFlag != 0 && statusFlag != 1))
                        return Error<EthereumTxObservation>(
                            $"The receipt for {normalized} carries no readable `status` field, so success cannot be "
                            + "distinguished from revert. Refusing to guess.");

                    if (!TryReadStringProperty(receipt, "transactionHash", out var receiptHash)
                        || !string.Equals(receiptHash, normalized, StringComparison.OrdinalIgnoreCase)
                        || !TryReadStringProperty(receipt, "blockHash", out var receiptBlockHash))
                        return Error<EthereumTxObservation>("Receipt transaction or block identity is unavailable.");
                    var canonical = await RpcReadAsync("eth_getBlockByNumber", new object?[] { EthereumHex.Quantity(blockNumber), false }, ct);
                    var finalized = await RpcReadAsync("eth_getBlockByNumber", new object?[] { "finalized", false }, ct);
                    if (canonical.IsError || finalized.IsError || canonical.IsNullResult || finalized.IsNullResult
                        || !TryReadStringProperty(canonical.Value, "hash", out var canonicalHash)
                        || !string.Equals(canonicalHash, receiptBlockHash, StringComparison.OrdinalIgnoreCase)
                        || !TryReadQuantityProperty(canonical.Value, "number", out var canonicalNumber) || canonicalNumber != blockNumber
                        || !TryReadQuantityProperty(finalized.Value, "number", out var finalNumber) || finalNumber < blockNumber)
                        return Ok(new EthereumTxObservation(normalized, ChainConfirmation.Pending, blockNumber, BigInteger.Zero, null,
                            "awaiting canonical finalized receipt evidence"));

                    TryReadQuantityProperty(receipt, "gasUsed", out var gasUsed);
                    var contractAddress = TryReadStringProperty(receipt, "contractAddress", out var deployed)
                        && EthereumAddress.TryParse(deployed, out var deployedChecksummed)
                        ? deployedChecksummed
                        : null;

                    var verdict = statusFlag.IsOne ? ChainConfirmation.Confirmed : ChainConfirmation.FailedOnChain;
                    return Ok(new EthereumTxObservation(
                        normalized, verdict, blockNumber, gasUsed, contractAddress,
                        verdict == ChainConfirmation.Confirmed
                            ? $"mined in block {blockNumber}"
                            : $"mined in block {blockNumber} and REVERTED"));
                }
            }

            // No receipt. The transaction may still be in the mempool — that is an
            // observably in-flight Pending, which is safe to wait on. Anything else is
            // Unknown (dropped, never broadcast, or an RPC that cannot see it).
            var poolResponse = await RpcReadAsync("eth_getTransactionByHash", new object?[] { normalized }, ct);
            if (poolResponse.IsError)
                return Error<EthereumTxObservation>(
                    $"The mempool lookup for {normalized} failed: {poolResponse.ErrorMessage}");

            if (!poolResponse.IsNullResult && poolResponse.Value.ValueKind == JsonValueKind.Object)
                return Ok(new EthereumTxObservation(
                    normalized, ChainConfirmation.Pending, BigInteger.Zero, BigInteger.Zero, null,
                    "known to the node but not yet mined"));

            return Error<EthereumTxObservation>(
                $"Transaction {normalized} is not observable on {ActiveNetwork}: the node has no receipt and no "
                + "mempool record for it (never broadcast, dropped, or outside this RPC's visibility).");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Error<EthereumTxObservation>($"The status probe for {txHash} failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<object>> QueryContractAsync(
        AZOA.WebAPI.Models.Deployments.EvmReadCall request, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(request.ContractAddress, out var contract))
            return Error<object>("A valid Ethereum contract address is required");

        var signature = EthereumAbi.ParseSignature(request.Method);
        if (signature is null)
            return Error<object>(
                $"'{request.Method}' is not an encodable function signature. Pass the full signature, e.g. "
                + "\"balanceOf(address)\". Tuple/struct parameters are not supported from an untyped argument bag.");

        var values = request.Arguments;
        if (values.Count != signature.Parameters.Length)
            return Error<object>(
                $"'{signature.Canonical}' takes {signature.Parameters.Length} argument(s) but {values.Count} were supplied.");

        var encodedArguments = new List<object>();
        for (var i = 0; i < values.Count; i++)
        {
            if (!EthereumAbi.TryCoerceArgument(signature.Parameters[i].Type, values[i], out var coerced, out var error))
                return Error<object>($"Argument {i} of {signature.Canonical}: {error}");
            encodedArguments.Add(coerced);
        }

        string callData;
        try
        {
            callData = EthereumAbi.EncodeCall(signature, encodedArguments.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return Error<object>($"Failed to ABI-encode the call to {signature.Canonical}: {ex.Message}", ex);
        }

        var caller = EthereumAddress.TryParse(request.FromAddress, out var parsedCaller) ? parsedCaller : null;
        var call = await CallReadAsync(contract, callData, caller, ct);
        if (call.IsError)
            return Error<object>(call.Message, call.Exception);

        var result = new Dictionary<string, object>
        {
            ["chain"] = ChainType,
            ["contractAddress"] = contract,
            ["function"] = signature.Canonical,
            ["returnData"] = call.Result ?? "0x",
        };

        var outputTypes = request.OutputTypes;
        if (outputTypes.Count > 0)
        {
            var outputs = outputTypes
                .Select((type, index) => new Nethereum.ABI.Model.Parameter(type, index + 1))
                .ToArray();

            if (!EthereumAbi.TryDecodeReturn(call.Result, outputs, out var decoded))
                return Error<object>(
                    $"The call to {signature.Canonical} returned data that does not decode as "
                    + $"({string.Join(',', outputTypes)}). Refusing to report a guessed value.");

            // BigInteger is rendered as a decimal string so a uint256 survives JSON
            // serialization exactly instead of being coerced to a double.
            result["decoded"] = decoded
                .Select(output => output.Result is BigInteger big
                    ? (object?)big.ToString(CultureInfo.InvariantCulture)
                    : output.Result)
                .ToList();
        }

        return Ok<object>(result, $"Read {signature.Canonical} on {contract}");
    }

    // ─── Chain info ───

    public override async Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(
        CancellationToken ct = default)
    {
        try
        {
            var chainId = await GetChainIdAsync(ct);
            var blockNumber = await ReadQuantityAsync("eth_blockNumber", Array.Empty<object?>(), "the head block number", ct);
            var fees = await GetFeeDataAsync(ct);

            var info = new Dictionary<string, object>
            {
                ["chain"] = ChainType,
                ["network"] = ActiveNetwork.ToString(),
                ["chainId"] = chainId.IsError ? "unknown" : chainId.Result.ToString(CultureInfo.InvariantCulture),
                ["blockNumber"] = blockNumber.IsError ? "unknown" : blockNumber.Result.ToString(CultureInfo.InvariantCulture),
                ["baseFeePerGasWei"] = fees.IsError ? "unknown" : fees.Result!.BaseFeePerGas.ToString(CultureInfo.InvariantCulture),
                ["maxPriorityFeePerGasWei"] = fees.IsError ? "unknown" : fees.Result!.MaxPriorityFeePerGas.ToString(CultureInfo.InvariantCulture),
                ["maxFeePerGasWei"] = fees.IsError ? "unknown" : fees.Result!.MaxFeePerGas.ToString(CultureInfo.InvariantCulture),
                ["transactionType"] = "EIP-1559 (type 0x02)",
                ["rpcEndpoint"] = RpcHttpClient.BaseAddress?.ToString() ?? string.Empty,
                ["time"] = DateTime.UtcNow,
            };

            return Ok(info, "Chain info retrieved");
        }
        catch (Exception ex)
        {
            return Error<Dictionary<string, object>>($"Chain info failed: {ex.Message}", ex);
        }
    }

    // ─── Cross-Chain Bridge ───
    // Ethereum implements NEITHER IBridgeSourceLeg NOR IBridgeTargetLeg, and that absence
    // is the whole refusal: no reviewed Ethereum bridge lifecycle exists — no proof that a
    // configured vault contract is controlled by the signing key, no canonical wrapped-token
    // contract or mint authority, and no lock/mint/burn/release pairing. The orchestrator
    // resolves the legs through TryGetModule and fails closed when they are missing, so no
    // refusal body remains here that could regress into a fabricated success.
    //
    // This is NOT a signer problem. The EIP-1559 value pipeline and its signer are real and
    // unaffected. See Providers/Blockchain/AGENTS.md §bridge.

    // ─── IEthereumValueModule (the BigInteger-native surface) ───

    /// <inheritdoc/>
    public async Task<AZOAResult<BigInteger>> GetNativeBalanceWeiAsync(string address, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(address, out var owner))
            return Error<BigInteger>("A valid Ethereum address is required");

        // A failed RPC read is NOT a zero balance: reporting 0 for an unreachable node
        // would let a caller believe the account is empty.
        return await ReadQuantityAsync(
            "eth_getBalance", new object?[] { owner, "latest" }, $"the ETH balance of {owner}", ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<BigInteger>> GetErc20BalanceAsync(
        string contractAddress, string ownerAddress, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(contractAddress, out var contract))
            return Error<BigInteger>("A valid ERC-20 contract address is required");
        if (!EthereumAddress.TryParse(ownerAddress, out var owner))
            return Error<BigInteger>("A valid Ethereum owner address is required");

        var hasCode = await ContractHasCodeAsync(contract, ct);
        if (hasCode.IsError)
            return Error<BigInteger>(hasCode.Message, hasCode.Exception);
        if (!hasCode.Result)
            return Error<BigInteger>(
                $"{contract} holds no code on {ActiveNetwork}, so it has no ERC-20 balance to read.");

        var call = await CallReadAsync(contract, EthereumAbi.EncodeCall(EthereumAbi.BalanceOfSignature, owner), owner, ct);
        if (call.IsError)
            return Error<BigInteger>(call.Message, call.Exception);

        if (!EthereumAbi.TryDecodeUInt256(call.Result, out var balance))
            return Error<BigInteger>(
                $"balanceOf({owner}) on {contract} returned '{call.Result}', which does not decode as a uint256.");

        return Ok(balance);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> TransferNativeAsync(
        string fromAddress, string toAddress, BigInteger weiAmount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(fromAddress, out var from))
            return Error<string>("A valid Ethereum sender address is required");
        if (!EthereumAddress.IsNonZero(toAddress) || !EthereumAddress.TryParse(toAddress, out var to))
            return Error<string>("A valid non-zero Ethereum recipient address is required");
        if (weiAmount <= BigInteger.Zero)
            return Error<string>("Transfer amount must be positive");
        if (weiAmount > EthereumHex.MaxUInt256)
            return Error<string>("Transfer amount exceeds the uint256 value field.");

        var opLabel = $"transfer {weiAmount} wei from {from} to {to}";
        return await BuildSignSubmitAsync(
            from, to, weiAmount, Array.Empty<byte>(), opLabel, signingContext ?? SigningContext.Platform, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> TransferErc20Async(
        string contractAddress, string fromAddress, string toAddress, BigInteger amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(contractAddress, out var contract))
            return Error<string>("A valid ERC-20 contract address is required");
        if (!EthereumAddress.TryParse(fromAddress, out var from))
            return Error<string>("A valid Ethereum sender address is required");
        if (!EthereumAddress.IsNonZero(toAddress) || !EthereumAddress.TryParse(toAddress, out var to))
            return Error<string>("A valid non-zero Ethereum recipient address is required");

        var guard = await GuardErc20CallAsync(contract, amount, "transfer", ct);
        if (guard.IsError)
            return Error<string>(guard.Message, guard.Exception);

        var callData = EthereumAbi.EncodeCall(EthereumAbi.TransferSignature, to, amount);
        if (!EthereumHex.TryParseData(callData, out var data))
            return Error<string>("Failed to encode the ERC-20 transfer calldata.");

        var opLabel = $"transfer {amount} base units of ERC-20 {contract} from {from} to {to}";
        return await BuildSignSubmitAsync(
            from, contract, BigInteger.Zero, data, opLabel, signingContext ?? SigningContext.Platform, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> MintErc20Async(
        string contractAddress, string minterAddress, string recipientAddress, BigInteger amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(contractAddress, out var contract))
            return Error<string>("A valid ERC-20 contract address is required");
        if (!EthereumAddress.TryParse(minterAddress, out var minter))
            return Error<string>("A valid Ethereum minter address is required");
        if (!EthereumAddress.IsNonZero(recipientAddress) || !EthereumAddress.TryParse(recipientAddress, out var recipient))
            return Error<string>("A valid non-zero Ethereum recipient address is required");

        var guard = await GuardErc20CallAsync(contract, amount, "mint", ct);
        if (guard.IsError)
            return Error<string>(guard.Message, guard.Exception);

        var callData = EthereumAbi.EncodeCall(EthereumAbi.MintSignature, recipient, amount);
        if (!EthereumHex.TryParseData(callData, out var data))
            return Error<string>("Failed to encode the ERC-20 mint calldata.");

        var opLabel = $"mint {amount} base units of ERC-20 {contract} to {recipient}";
        return await BuildSignSubmitAsync(
            minter, contract, BigInteger.Zero, data, opLabel, signingContext ?? SigningContext.Platform, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> BurnErc20Async(
        string contractAddress, string holderAddress, BigInteger amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (!EthereumAddress.TryParse(contractAddress, out var contract))
            return Error<string>("A valid ERC-20 contract address is required");
        if (!EthereumAddress.TryParse(holderAddress, out var holder))
            return Error<string>("A valid Ethereum holder address is required");

        var guard = await GuardErc20CallAsync(contract, amount, "burn", ct);
        if (guard.IsError)
            return Error<string>(guard.Message, guard.Exception);

        var callData = EthereumAbi.EncodeCall(EthereumAbi.BurnSignature, amount);
        if (!EthereumHex.TryParseData(callData, out var data))
            return Error<string>("Failed to encode the ERC-20 burn calldata.");

        var opLabel = $"burn {amount} base units of ERC-20 {contract} held by {holder}";
        return await BuildSignSubmitAsync(
            holder, contract, BigInteger.Zero, data, opLabel, signingContext ?? SigningContext.Platform, ct);
    }

    /// <summary>
    /// Shared ERC-20 preconditions. The code check is load-bearing, not cosmetic: a call
    /// to an address with NO code succeeds and does nothing, so an ERC-20 "transfer" to a
    /// mistyped contract address would report success while moving nothing.
    /// </summary>
    private async Task<AZOAResult<bool>> GuardErc20CallAsync(
        string contract, BigInteger amount, string operation, CancellationToken ct)
    {
        if (amount <= BigInteger.Zero)
            return Error<bool>($"ERC-20 {operation} amount must be positive");
        if (amount > EthereumHex.MaxUInt256)
            return Error<bool>($"ERC-20 {operation} amount exceeds the uint256 argument slot.");

        var hasCode = await ContractHasCodeAsync(contract, ct);
        if (hasCode.IsError)
            return hasCode;

        return hasCode.Result
            ? Ok(true)
            : Error<bool>(
                $"Cannot {operation}: {contract} holds no code on {ActiveNetwork}. A call to a codeless address "
                + "succeeds and does nothing, so this would report a token movement that never happened.");
    }

    private string? ResolveMintContract(string? tokenUri)
    {
        if (EthereumAddress.TryParse(tokenUri, out var fromArgument))
            return fromArgument;

        var configured = _config.GetValue<string>(PlatformTokenContractConfigPath);
        return EthereumAddress.TryParse(configured, out var fromConfig) ? fromConfig : null;
    }

    /// <summary>
    /// Resolves the account that will sign, for a method whose interface signature does
    /// not carry it. A platform op signs from the configured platform account; a per-user
    /// op signs from the user's own address, which is the caller-supplied one.
    /// </summary>
    private Task<AZOAResult<string>> ResolveSignerAddressAsync(
        string fallbackAddress, SigningContext? signingContext, CancellationToken ct)
    {
        var ctx = signingContext ?? SigningContext.Platform;
        if (!ctx.IsPlatform)
            return Task.FromResult(Ok(fallbackAddress));

        var configured = _config.GetValue<string>(PlatformPrivateKeyConfigPath);
        if (!string.IsNullOrWhiteSpace(configured) && TryDeriveAddress(configured, out var platformAddress))
            return Task.FromResult(Ok(platformAddress));

        // The chain-aware custody platform door derives Ethereum's key from this SAME config
        // path (EthereumPlatformKeyDerivation.ConfigPath), so when it is unset neither door can
        // name the sending account. See AGENTS.md §custody.
        return Task.FromResult(Error<string>(
            "Cannot determine the Ethereum platform signing account: no key is configured at "
            + $"'{PlatformPrivateKeyConfigPath}', which is also the path the custody platform door derives this "
            + "chain's platform key from. Refusing to guess the sending account."));
    }

    // ─── Chain reads used by the pipeline ───

    private async Task<AZOAResult<bool>> ContractHasCodeAsync(string address, CancellationToken ct)
    {
        try
        {
            var response = await RpcReadAsync("eth_getCode", new object?[] { address, "latest" }, ct);
            if (response.IsError)
                return Error<bool>($"Could not read the code at {address}: {response.ErrorMessage}");
            if (!response.TryGetString(out var code) || !EthereumHex.TryParseData(code, out var bytes))
                return Error<bool>($"Could not read the code at {address}: the node returned '{response.RawText}'.");

            return Ok(bytes.Length > 0);
        }
        catch (Exception ex)
        {
            return Error<bool>($"Could not read the code at {address}: {ex.Message}", ex);
        }
    }

    private async Task<AZOAResult<string>> CallReadAsync(
        string contract, string callData, string? from, CancellationToken ct)
    {
        var call = new Dictionary<string, object> { ["to"] = contract, ["data"] = callData };
        if (!string.IsNullOrWhiteSpace(from))
            call["from"] = from;

        try
        {
            var response = await RpcReadAsync("eth_call", new object?[] { call, "latest" }, ct);
            if (response.IsError)
                return Error<string>($"eth_call to {contract} failed: {response.ErrorMessage}");
            if (!response.TryGetString(out var data))
                return Error<string>($"eth_call to {contract} returned '{response.RawText}', not hex data.");

            return Ok(data);
        }
        catch (Exception ex)
        {
            return Error<string>($"eth_call to {contract} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// EIP-1559 fee data from chain truth: the head block's base fee plus the node's
    /// priority-fee suggestion. A chain with no <c>baseFeePerGas</c> is pre-1559 and is
    /// refused rather than silently downgraded to a legacy <c>gasPrice</c> transaction.
    /// </summary>
    private async Task<AZOAResult<EthereumFeeData>> GetFeeDataAsync(CancellationToken ct)
    {
        BigInteger baseFee;
        try
        {
            var block = await RpcReadAsync("eth_getBlockByNumber", new object?[] { "latest", false }, ct);
            if (block.IsError)
                return Error<EthereumFeeData>($"Could not read the head block for fee data: {block.ErrorMessage}");
            if (block.IsNullResult || block.Value.ValueKind != JsonValueKind.Object)
                return Error<EthereumFeeData>("Could not read the head block for fee data: the node returned no block.");

            if (!TryReadQuantityProperty(block.Value, "baseFeePerGas", out baseFee))
                return Error<EthereumFeeData>(
                    "The head block carries no baseFeePerGas, so this endpoint is not an EIP-1559 chain. This "
                    + "provider only builds type-2 transactions and will not fall back to a legacy gasPrice.");
        }
        catch (Exception ex)
        {
            return Error<EthereumFeeData>($"Could not read the head block for fee data: {ex.Message}", ex);
        }

        // eth_maxPriorityFeePerGas is optional; a node that does not implement it falls
        // back to the configured default rather than failing the whole transaction.
        var tip = _defaultPriorityFeeWei;
        try
        {
            var suggested = await RpcReadAsync("eth_maxPriorityFeePerGas", Array.Empty<object?>(), ct);
            if (!suggested.IsError && suggested.TryGetString(out var hex) && EthereumHex.TryParseQuantity(hex, out var value))
                tip = value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "eth_maxPriorityFeePerGas unavailable; using the configured default tip.");
        }

        // Two base fees of headroom is the standard allowance for the 12.5%-per-block
        // base-fee ramp; the tip is what the block producer actually keeps.
        var maxFee = (baseFee * _baseFeeMultiplier) + tip;

        if (_maxFeePerGasWeiCap > BigInteger.Zero && maxFee > _maxFeePerGasWeiCap)
            return Error<EthereumFeeData>(
                $"Computed maxFeePerGas {maxFee} wei exceeds the configured ceiling {_maxFeePerGasWeiCap} wei "
                + $"(base fee {baseFee}, tip {tip}). Refusing to sign a transaction that could burn an unbounded fee; "
                + $"raise '{MaxFeePerGasWeiCapConfigPath}' deliberately if this fee level is intended.");

        return Ok(new EthereumFeeData(baseFee, tip, maxFee));
    }

    /// <summary>
    /// Gas limit from <c>eth_estimateGas</c> plus a configured buffer. Never hardcoded:
    /// a failing estimate is a REVERT the node has already simulated, so refusing here
    /// costs nothing while broadcasting anyway would burn the whole limit.
    /// </summary>
    private async Task<AZOAResult<BigInteger>> EstimateGasAsync(
        string from, string? to, BigInteger value, byte[] data, string opLabel, CancellationToken ct)
    {
        var call = new Dictionary<string, object> { ["from"] = from };
        if (!string.IsNullOrWhiteSpace(to))
            call["to"] = to;
        if (value > BigInteger.Zero)
            call["value"] = EthereumHex.Quantity(value);
        if (data.Length > 0)
            call["data"] = EthereumHex.Data(data);

        try
        {
            var response = await RpcReadAsync("eth_estimateGas", new object?[] { call, "latest" }, ct);
            if (response.IsError)
                return Error<BigInteger>(
                    $"Cannot {opLabel}: the node refused to estimate gas, which means the transaction would revert "
                    + $"— {response.ErrorMessage}. Refusing to broadcast a transaction that is already known to fail.");

            if (!response.TryGetString(out var hex) || !EthereumHex.TryParseQuantity(hex, out var estimate) || estimate.IsZero)
                return Error<BigInteger>(
                    $"Cannot {opLabel}: the node returned '{response.RawText}' for eth_estimateGas, not a usable gas amount.");

            var buffered = estimate * _gasLimitBufferPercent / 100;
            if (buffered < estimate)
                buffered = estimate;

            if (buffered > _maxGasLimit)
                return Error<BigInteger>(
                    $"Cannot {opLabel}: the estimated gas limit {buffered} exceeds the configured ceiling "
                    + $"{_maxGasLimit}. Refusing to sign an unbounded-cost transaction; raise "
                    + $"'{MaxGasLimitConfigPath}' deliberately if this is intended.");

            return Ok(buffered);
        }
        catch (Exception ex)
        {
            return Error<BigInteger>($"Cannot {opLabel}: gas estimation failed — {ex.Message}", ex);
        }
    }

    // ─── Build → sign → submit → confirm orchestration ───

    /// <summary>Admits a durable nonce, persists signed wire and reports authoritative finality.</summary>
    private async Task<AZOAResult<string>> BuildSignSubmitAsync(
        string fromAddress, string? toAddress, BigInteger value, byte[] data,
        string opLabel, SigningContext signingContext, CancellationToken ct)
    {
        var submitted = await BuildSignSubmitCoreAsync(fromAddress, toAddress, value, data, opLabel, signingContext, ct);
        if (submitted.IsError || submitted.Result is null)
            return Error<string>(submitted.Message, submitted.Exception);

        if (submitted.Result.PendingConfirmation)
            return Ok(submitted.Result.TxHash,
                $"{OperationStatus.PendingConfirmationMarker}: Ethereum {opLabel} (tx {submitted.Result.TxHash}) "
                + "durably prepared and awaiting authoritative confirmation.");

        return Ok(submitted.Result.TxHash,
            $"Ethereum {opLabel} confirmed in block {submitted.Result.BlockNumber} (tx {submitted.Result.TxHash})");
    }

    private async Task<AZOAResult<SubmittedEthereumTxn>> BuildSignSubmitCoreAsync(
        string fromAddress, string? toAddress, BigInteger value, byte[] data,
        string opLabel, SigningContext signingContext, CancellationToken ct)
    {
        var prepared = await PrepareCallAsync("legacy:" + Guid.NewGuid().ToString("N"), fromAddress,
            toAddress, value, data, opLabel, signingContext, ct);
        if (prepared.IsError || prepared.Result is null)
            return Error<SubmittedEthereumTxn>(prepared.Message);
        try { await SubmitPreparationAsync(prepared.Result, ct); }
        catch (HttpRequestException)
        {
            return Ok(new SubmittedEthereumTxn(prepared.Result.TransactionId!, BigInteger.Zero, null, PendingConfirmation: true),
                "Ethereum submission remains durably pending; reconcile the original transaction.");
        }
        var observation = await ObservePreparationAsync(prepared.Result, ct);
        if (!observation.IsError && observation.Result == ChainConfirmation.Confirmed)
            return await WaitForReceiptAsync(prepared.Result.TransactionId!, opLabel, ct);
        if (!observation.IsError && observation.Result == ChainConfirmation.FailedOnChain)
            return Error<SubmittedEthereumTxn>("The prepared Ethereum call reverted in a finalized block: " + prepared.Result.TransactionId);
        return Ok(new SubmittedEthereumTxn(prepared.Result.TransactionId!, BigInteger.Zero, null, PendingConfirmation: true));
    }

    private async Task<AZOAResult<SubmittedEthereumTxn>> WaitForReceiptAsync(
        string txHash, string opLabel, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _confirmationPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // A probe that errors is a transient RPC hiccup or a not-yet-visible tx —
            // keep waiting. The broadcast already happened and must not be repeated.
            var probe = await ProbeTransactionAsync(txHash, ct);
            if (!probe.IsError && probe.Result is { } observation)
            {
                switch (observation.Verdict)
                {
                    case ChainConfirmation.Confirmed:
                        return Ok(new SubmittedEthereumTxn(
                            txHash, observation.BlockNumber, observation.ContractAddress, PendingConfirmation: false));

                    case ChainConfirmation.FailedOnChain:
                        // The transaction landed and the EVM reverted it. That is a real
                        // on-chain failure with a real hash — report it, never retry.
                        return Error<SubmittedEthereumTxn>(
                            $"Transaction {txHash} was mined in block {observation.BlockNumber} but REVERTED while "
                            + $"trying to {opLabel}. The fee was spent; the effect did not happen.");
                }
            }

            await Task.Delay(_confirmationPollDelayMs, ct);
        }

        // The tx WAS broadcast; it simply has not been mined inside the poll window.
        // Returning an error here would record the op Failed WITHOUT a hash, and a
        // slow-but-valid transaction would become permanently false-Failed.
        return Ok(
            new SubmittedEthereumTxn(txHash, BigInteger.Zero, ContractAddress: null, PendingConfirmation: true),
            $"Transaction {txHash} submitted but not mined within {_confirmationPollAttempts} polls while trying to "
            + $"{opLabel}; recorded pending — reconcile via GetTransactionConfirmationAsync.");
    }

    // ─── Nonce ───

    /// <summary>
    /// Resolves the signing key through the audited custody choke point and returns the
    /// signed envelope. A per-user context that does not resolve is a CLEAR ERROR — it
    /// never falls back to the platform key.
    /// </summary>
    private async Task<AZOAResult<byte[]>> SignViaCustodyAsync(
        byte[] canonicalPayload, string opLabel, string expectedSender, SigningContext ctx)
    {
        Func<byte[], Task<AZOAResult<byte[]>>> sign = keyBytes =>
        {
            if (keyBytes is null || keyBytes.Length != EthereumTransactionSigner.PrivateKeyLength)
                return Task.FromResult(Error<byte[]>(WrongShapedKeyMessage(opLabel, ctx, keyBytes?.Length ?? 0)));

            // THE load-bearing check for Ethereum. An Algorand Ed25519 seed is ALSO 32
            // bytes, so the length test above cannot tell the two apart — but a
            // secp256k1 interpretation of those bytes controls a completely different
            // account. An EIP-1559 transaction carries no `from` field (the sender is
            // recovered from the signature), so signing with the wrong key would silently
            // spend from an account the caller never named, at a nonce read for another.
            if (!TryDeriveAddress(keyBytes, out var resolvedSender))
                return Task.FromResult(Error<byte[]>(
                    $"Cannot {opLabel}: the resolved signing key is not a valid secp256k1 scalar."));

            if (!EthereumAddress.AreSame(resolvedSender, expectedSender))
                return Task.FromResult(Error<byte[]>(WrongSenderMessage(opLabel, ctx, expectedSender)));

            using var material = new SigningKeyMaterial(keyBytes);
            return Task.FromResult(_signerFactory!.GetSigner(ChainType).Sign(canonicalPayload, material));
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
                $"Cannot {opLabel}: the Ethereum platform signing fallback is disabled in {aspnetEnv}; "
                + "the custody service must be wired.");

        var configured = _config.GetValue<string>(PlatformPrivateKeyConfigPath);
        if (string.IsNullOrWhiteSpace(configured))
            return Error<byte[]>(
                $"Cannot {opLabel}: no custody service is wired and no Ethereum platform signing key is configured "
                + $"(set {PlatformPrivateKeyConfigPath} to a 0x-prefixed 32-byte secp256k1 private key).");

        if (!EthereumHex.TryParseData(configured.Trim(), out var key)
            || key.Length != EthereumTransactionSigner.PrivateKeyLength)
            return Error<byte[]>(
                $"Cannot {opLabel}: '{PlatformPrivateKeyConfigPath}' is not a 0x-prefixed "
                + $"{EthereumTransactionSigner.PrivateKeyLength}-byte private key.");

        try
        {
            return await sign(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Derives the Ethereum address a private key controls; never logs either.</summary>
    private static bool TryDeriveAddress(byte[] privateKey, out string address)
    {
        address = string.Empty;
        try
        {
            address = new EthECKey(privateKey, isPrivate: true).GetPublicAddress();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryDeriveAddress(string privateKeyHex, out string address)
    {
        address = string.Empty;
        if (!EthereumHex.TryParseData(privateKeyHex.Trim(), out var key)
            || key.Length != EthereumTransactionSigner.PrivateKeyLength)
            return false;

        try
        {
            return TryDeriveAddress(key, out address);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private string WrongShapedKeyMessage(string opLabel, SigningContext ctx, int actualLength)
    {
        var cause = ctx.IsPlatform
            ? $"the shared custody platform door resolves an Algorand-format key from "
              + $"'{KeyCustodyService.PlatformMnemonicConfigPath}'; an Ethereum platform op needs a chain-aware "
              + $"platform key (see '{PlatformPrivateKeyConfigPath}')"
            : "the resolved wallet does not hold a secp256k1 secret — check that the wallet's chain matches this provider";

        return $"Cannot {opLabel}: the resolved signing key is {actualLength} bytes, not the "
            + $"{EthereumTransactionSigner.PrivateKeyLength}-byte secp256k1 private key Ethereum needs — {cause}. "
            + "Refusing to sign with a mismatched key.";
    }

    private string WrongSenderMessage(string opLabel, SigningContext ctx, string expectedSender)
    {
        var cause = ctx.IsPlatform
            ? "a platform signing context that does not name its chain still takes the custody door's legacy "
              + $"path and resolves an Algorand Ed25519 seed from '{KeyCustodyService.PlatformMnemonicConfigPath}' — "
              + "the same 32-byte length as a secp256k1 key, so only this check catches it. A chain-stamped "
              + $"Ethereum platform op resolves '{PlatformPrivateKeyConfigPath}' instead; check that the key "
              + "configured there controls the expected sender"
            : "the resolved wallet's stored address does not match the key it holds. WalletKeyService "
              + "now derives an Ethereum wallet's address from its own secp256k1 scalar through the same "
              + "call this provider uses, so an AZOA-generated wallet should never reach here — if one "
              + "does, the wallet record and its key have genuinely diverged";

        return $"Cannot {opLabel}: the resolved signing key does not control {expectedSender}. An EIP-1559 "
            + "transaction carries no sender field — it would have spent from whatever account that key controls, "
            + $"at a nonce read for another. {cause}. Refusing to sign.";
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

    // ─── JSON element readers ───

    private static bool TryReadQuantityProperty(JsonElement element, string name, out BigInteger value)
    {
        value = BigInteger.Zero;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && EthereumHex.TryParseQuantity(property.GetString(), out value);
    }

    private static bool TryReadStringProperty(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    // ─── Pipeline value types ───

    private sealed record EthereumFeeData(BigInteger BaseFeePerGas, BigInteger MaxPriorityFeePerGas, BigInteger MaxFeePerGas);

    private sealed record SubmittedEthereumTxn(
        string TxHash, BigInteger BlockNumber, string? ContractAddress, bool PendingConfirmation);

    private sealed record EthereumTxObservation(
        string TxHash, ChainConfirmation Verdict, BigInteger BlockNumber, BigInteger GasUsed,
        string? ContractAddress, string Detail);

    // ─── Ethereum JSON-RPC envelope ───

    private sealed class EthRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public object?[] Params { get; set; } = Array.Empty<object?>();
    }

    /// <summary>
    /// A parsed JSON-RPC response. <see cref="IsNullResult"/> is deliberately distinct
    /// from an error: <c>eth_getTransactionReceipt</c> answers <c>result: null</c> for a
    /// transaction that is simply not mined yet, which must never read as a verdict.
    /// </summary>
    private readonly struct EthRpcResult
    {
        private EthRpcResult(bool isError, string errorMessage, bool isNullResult, JsonElement value, string rawText)
        {
            IsError = isError;
            ErrorMessage = errorMessage;
            IsNullResult = isNullResult;
            Value = value;
            RawText = rawText;
        }

        public bool IsError { get; }
        public string ErrorMessage { get; }
        public bool IsNullResult { get; }
        public JsonElement Value { get; }
        public string RawText { get; }

        public bool TryGetString(out string value)
        {
            value = string.Empty;
            if (IsError || IsNullResult || Value.ValueKind != JsonValueKind.String)
                return false;

            value = Value.GetString() ?? string.Empty;
            return true;
        }

        public static EthRpcResult Parse(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Failure("the node returned an empty response body");

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return Failure($"the node returned a {root.ValueKind} body, not a JSON-RPC object");

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
                    var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                        ? parsedCode.ToString(CultureInfo.InvariantCulture)
                        : "?";
                    return Failure($"[{code}] {message ?? error.GetRawText()}");
                }

                if (!root.TryGetProperty("result", out var result))
                    return Failure("the node returned neither a result nor an error");

                return new EthRpcResult(
                    isError: false,
                    errorMessage: string.Empty,
                    isNullResult: result.ValueKind == JsonValueKind.Null,
                    // Cloned so it outlives the JsonDocument this method disposes.
                    value: result.Clone(),
                    rawText: result.ValueKind == JsonValueKind.Null ? "null" : result.GetRawText());
            }
            catch (JsonException ex)
            {
                return Failure($"the node returned an unparseable JSON-RPC body: {ex.Message}");
            }
        }

        private static EthRpcResult Failure(string message) =>
            new(isError: true, errorMessage: message, isNullResult: false, value: default, rawText: string.Empty);
    }
}
