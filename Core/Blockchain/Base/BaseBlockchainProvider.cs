using System.Net.Sockets;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Base;

/// <summary>
/// Controls how <see cref="BaseBlockchainProvider.ExecuteWithRetryAsync{T}"/>
/// treats retryable failures.
/// </summary>
public enum RetrySafety
{
    /// <summary>
    /// Default / legacy behaviour. The wrapped operation is idempotent (a
    /// read, a quote, a status poll, a balance query): re-invoking it on any
    /// retryable transport error is harmless, so retry on 5xx / 429 / timeout.
    /// </summary>
    Idempotent = 0,

    /// <summary>
    /// The wrapped operation BROADCASTS a transaction (a real, irreversible
    /// chain submit). After the request leaves the client, a timeout / null
    /// HTTP status / 5xx is AMBIGUOUS — the node may already have accepted and
    /// is propagating the tx. Re-invoking <c>operation()</c> in that state
    /// re-broadcasts ⇒ double spend. In this mode we retry ONLY on errors that
    /// are PROVABLY pre-broadcast (the request was never put on the wire, e.g.
    /// TCP connection refused before send). Every ambiguous post-send failure
    /// is rethrown unretried so the caller can reconcile against chain truth
    /// instead of silently re-sending.
    /// </summary>
    Broadcast = 1,
}

/// <summary>
/// Base class for blockchain providers with shared retry, error handling, and configuration logic.
/// Network binding is owned here so factory-created providers cannot be retargeted.
/// </summary>
public abstract class BaseBlockchainProvider : IBlockchainProvider
{
    private readonly object _standaloneInitializationGate = new();
    private int _initializationState;
    private int _factoryBound;

    protected readonly IConfiguration _config;
    protected readonly ILogger _logger;
    protected BlockchainNetworkConfig _networkConfig = new();

    public abstract string ChainType { get; }
    public ChainNetwork ActiveNetwork { get; protected set; }

    protected BaseBlockchainProvider(IConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Initialize(BlockchainNetworkConfig config, ChainNetwork network)
    {
        if (Volatile.Read(ref _factoryBound) != 0)
            throw new InvalidOperationException(
                $"{ChainType} provider is factory-bound and cannot be reinitialized externally.");

        InitializeOnce(config, network);
    }

    /// <summary>Builds network-specific resources during the sole allowed initialization.</summary>
    protected virtual void OnInitialize(BlockchainNetworkConfig config, ChainNetwork network)
    {
    }

    /// <summary>True once the provider has a stable network binding.</summary>
    protected bool IsInitialized => Volatile.Read(ref _initializationState) != 0;

    /// <summary>Initializes a standalone provider lazily for direct unit-test or tool use.</summary>
    protected void EnsureStandaloneInitialized(
        Func<(BlockchainNetworkConfig Config, ChainNetwork Network)> resolveDefaultBinding)
    {
        ArgumentNullException.ThrowIfNull(resolveDefaultBinding);
        if (IsInitialized)
            return;

        lock (_standaloneInitializationGate)
        {
            if (IsInitialized)
                return;

            var (config, network) = resolveDefaultBinding();
            Initialize(config, network);
        }
    }

    internal void InitializeForFactory(BlockchainNetworkConfig config, ChainNetwork network)
    {
        if (IsInitialized)
            throw new InvalidOperationException(
                $"{ChainType} provider registration returned an already initialized instance. " +
                "Factory registrations must create an unbound provider.");

        if (Interlocked.CompareExchange(ref _factoryBound, 1, 0) != 0)
            throw new InvalidOperationException($"{ChainType} provider is already factory-bound.");

        InitializeOnce(config, network);
    }

    private void InitializeOnce(BlockchainNetworkConfig config, ChainNetwork network)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Enum.IsDefined(network))
            throw new ArgumentOutOfRangeException(nameof(network), network, "Unsupported blockchain network.");

        if (Interlocked.CompareExchange(ref _initializationState, 1, 0) != 0)
            throw new InvalidOperationException(
                $"{ChainType} provider is already initialized and cannot be rebound to {network}.");

        try
        {
            OnInitialize(config, network);
            _networkConfig = config;
            ActiveNetwork = network;
        }
        catch
        {
            Interlocked.Exchange(ref _initializationState, 0);
            throw;
        }

        _logger.LogInformation(
            "Initialized {ChainType} provider on {Network} with node {NodeUrl}",
            ChainType, network, config.NodeUrl);
    }

    public bool TryGetModule<T>(out T? module) where T : class, IBlockchainProviderModule
    {
        if (this is T t)
        {
            module = t;
            return true;
        }
        module = null;
        return false;
    }

    // ─── IBlockchainProvider virtual implementations (override in derived) ───

    public virtual Task<AZOAResult<string>> GetBalanceAsync(
        string address, string? tokenId = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} GetBalanceAsync not implemented"));
    }

    public virtual Task<AZOAResult<bool>> ValidateAddressAsync(
        string address, CancellationToken ct = default)
    {
        return Task.FromResult(new AZOAResult<bool>
        {
            IsError = true,
            Result = false,
            Message = $"{ChainType} ValidateAddressAsync not implemented"
        });
    }

    /// <summary>
    /// Conservative base default: a provider has no faucet unless it opts in. The
    /// chain-agnostic top-up caller reads this to short-circuit unsupported chains.
    /// </summary>
    public virtual bool SupportsFaucet => false;

    /// <summary>
    /// Base default for chains without a faucet path — returns a clear
    /// "not supported" error. Providers that top up override this (server-side
    /// dispense or client-side airdrop acknowledgement) and set
    /// <see cref="SupportsFaucet"/> to true.
    /// </summary>
    public virtual Task<AZOAResult<FaucetDispenseResult>> DispenseFromFaucetAsync(
        string toAddress, decimal amount, string? idempotencyKey = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<FaucetDispenseResult>($"Top-up not supported for chain {ChainType}."));
    }

    public virtual Task<AZOAResult<string>> MintAsync(
        string tokenUri, ulong amount, string assetType, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} MintAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> BurnAsync(
        string tokenId, ulong amount, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} BurnAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> TransferAsync(
        string tokenId, string fromAddress, string toAddress, ulong amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} TransferAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> ExchangeAsync(
        string sourceTokenId, string targetTokenId, string exchangeRate,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} ExchangeAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> SwapAsync(
        string tokenIn, string tokenOut, decimal amountIn, decimal minAmountOut,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} SwapAsync not implemented"));
    }

    public virtual Task<AZOAResult<Dictionary<string, object>>> GetTokenMetadataAsync(
        string tokenId, CancellationToken ct = default)
    {
        return Task.FromResult(Error<Dictionary<string, object>>($"{ChainType} GetTokenMetadataAsync not implemented"));
    }

    public virtual Task<AZOAResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
        string ownerAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<List<Dictionary<string, object>>>($"{ChainType} GetTokensByOwnerAsync not implemented"));
    }

    public virtual Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash, CancellationToken ct = default)
    {
        return Task.FromResult(Error<Dictionary<string, object>>($"{ChainType} GetTransactionStatusAsync not implemented"));
    }

    /// <summary>
    /// Conservative base default (blockchain-recovery-and-portable-wallets §1.2):
    /// derive the confirmation verdict from <see cref="GetTransactionStatusAsync"/>
    /// via <see cref="ChainTxClassifier"/>. A status probe that itself errors maps
    /// to <see cref="ChainConfirmation.Unknown"/> — never a false negative — so a
    /// flaky RPC can never cause a re-broadcast. Providers that can do a precise
    /// mempool lookup should override to sharpen Pending vs Unknown.
    /// </summary>
    public virtual async Task<AZOAResult<ChainConfirmation>> GetTransactionConfirmationAsync(
        string txHash, CancellationToken ct = default)
    {
        try
        {
            var status = await GetTransactionStatusAsync(txHash, ct);
            // Never propagate IsError outward as a hard failure: the classifier folds
            // an errored/absent probe into ChainConfirmation.Unknown, which the
            // reconcile-before-retry caller treats as "park, do not act".
            return new AZOAResult<ChainConfirmation> { Result = ChainTxClassifier.Classify(status) };
        }
        catch (Exception ex)
        {
            // A thrown probe (network timeout, socket exception) is as ambiguous as
            // an errored result: fold it into Unknown so a flaky RPC can never crash
            // the reconciler or be mistaken for a confirmed failure.
            return new AZOAResult<ChainConfirmation> { Result = ChainConfirmation.Unknown, Message = ex.Message };
        }
    }

    public virtual Task<AZOAResult<string>> DeployContractAsync(
        byte[] contractCode, string walletAddress,
        Dictionary<string, object>? args = null, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} DeployContractAsync not implemented"));
    }

    public virtual Task<AZOAResult<object>> CallContractAsync(
        string contractAddress, string method, Dictionary<string, object> args,
        string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<object>($"{ChainType} CallContractAsync not implemented"));
    }

    public virtual Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult(Error<Dictionary<string, object>>($"{ChainType} GetChainInfoAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> LockForBridgeAsync(
        string tokenId, string vaultAddress, ulong amount,
        string targetChain, string targetRecipient, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} LockForBridgeAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> MintWrappedAsync(
        string sourceChain, string sourceTokenId, string tokenUri,
        ulong amount, string recipientAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} MintWrappedAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> BurnWrappedAsync(
        string tokenId, ulong amount, string sourceChain,
        string sourceRecipient, string walletAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} BurnWrappedAsync not implemented"));
    }

    public virtual Task<AZOAResult<string>> ReleaseFromBridgeAsync(
        string tokenId, string vaultAddress, ulong amount,
        string recipientAddress, CancellationToken ct = default)
    {
        return Task.FromResult(Error<string>($"{ChainType} ReleaseFromBridgeAsync not implemented"));
    }

    // VerifyBridgeProofAsync removed (final-hardening-cutover B2): cross-chain proof
    // verification lives ONLY on the hardened WormholeAdapter /
    // Secp256k1VaaSignatureVerifier path. No provider-level verifier — an always-true
    // one previously lied here. See Providers/Blockchain/AGENTS.md.

    public virtual bool SupportsBridging => false;

    // ─── Shared utility methods ───

    protected async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        int initialDelayMs = 1000,
        CancellationToken ct = default,
        RetrySafety safety = RetrySafety.Idempotent)
    {
        int retryCount = 0;
        int delayMs = initialDelayMs;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (retryCount < maxRetries && IsRetryable(ex, safety))
            {
                retryCount++;
                _logger.LogWarning(
                    ex,
                    "Retry {Retry}/{Max} for {ChainType} operation after {Delay}ms",
                    retryCount, maxRetries, ChainType, delayMs);
                await Task.Delay(delayMs, ct);
                delayMs = (int)(delayMs * 1.5);
            }
        }
    }

    protected async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        int maxRetries = 3,
        int initialDelayMs = 1000,
        CancellationToken ct = default,
        RetrySafety safety = RetrySafety.Idempotent)
    {
        int retryCount = 0;
        int delayMs = initialDelayMs;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await operation();
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (retryCount < maxRetries && IsRetryable(ex, safety))
            {
                retryCount++;
                _logger.LogWarning(
                    ex,
                    "Retry {Retry}/{Max} for {ChainType} operation after {Delay}ms",
                    retryCount, maxRetries, ChainType, delayMs);
                await Task.Delay(delayMs, ct);
                delayMs = (int)(delayMs * 1.5);
            }
        }
    }

    /// <summary>
    /// Idempotent-mode retry classifier (legacy behaviour). Overridable by
    /// derived providers. Retries on transport ambiguity (null status / 5xx /
    /// 429) because re-invoking an idempotent operation is harmless.
    /// </summary>
    protected virtual bool IsRetryable(Exception ex)
    {
        return ex is HttpRequestException httpEx &&
               (httpEx.StatusCode == null || (int)httpEx.StatusCode >= 500 || (int)httpEx.StatusCode == 429);
    }

    /// <summary>
    /// Safety-aware retry decision.
    /// <para>
    /// <see cref="RetrySafety.Idempotent"/>: defers to the overridable
    /// <see cref="IsRetryable(Exception)"/> — unchanged legacy behaviour, so
    /// every existing call site (which omits the parameter) is byte-for-byte
    /// equivalent.
    /// </para>
    /// <para>
    /// <see cref="RetrySafety.Broadcast"/>: a tx has (or may have) been put on
    /// the wire. Retrying re-broadcasts ⇒ DOUBLE SPEND. We therefore retry
    /// ONLY when we can PROVE the request never reached the node — i.e. the
    /// TCP connection itself failed before/while connecting (connection
    /// refused / DNS / no route). Any error that occurred after a connection
    /// was established (HTTP timeout, null status mid-flight, 5xx, 429, or any
    /// non-socket exception) is AMBIGUOUS: the node may already hold the tx.
    /// Those are NOT retried — the exception propagates so the caller drives
    /// reconciliation against chain truth instead of re-sending.
    /// </para>
    /// </summary>
    private bool IsRetryable(Exception ex, RetrySafety safety)
    {
        if (safety == RetrySafety.Idempotent)
            return IsRetryable(ex);

        // RetrySafety.Broadcast — provably pre-broadcast errors only.
        // A SocketException whose inner cause is a connection-establishment
        // failure means the HTTP request body was never delivered, so no tx
        // could have been accepted: safe to retry the broadcast.
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException se)
            {
                return se.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostNotFound
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.HostDown;
            }
        }

        // Everything else under Broadcast is ambiguous post-send: do NOT
        // auto-retry an irreversible broadcast. Surface for reconciliation.
        return false;
    }

    protected AZOAResult<T> Error<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "{ChainType} error: {Message}", ChainType, message);
        return new AZOAResult<T>
        {
            IsError = true,
            Message = message,
            Exception = ex
        };
    }

    protected AZOAResult<T> Ok<T>(T result, string message = "")
    {
        return new AZOAResult<T>
        {
            IsError = false,
            Result = result,
            Message = message
        };
    }
}
