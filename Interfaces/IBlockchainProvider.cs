using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

public interface IBlockchainProvider
{
    string ChainType { get; }
    ChainNetwork ActiveNetwork { get; }

    void Initialize(BlockchainNetworkConfig config, ChainNetwork network);

    /// <summary>
    /// Get a chain-specific capability module (e.g., IAlgorandASAModule, ISolanaMetaplexModule).
    /// </summary>
    bool TryGetModule<T>(out T? module) where T : class, IBlockchainProviderModule;

    // ─── Account / Wallet ───
    Task<AZOAResult<string>> GetBalanceAsync(string address, string? tokenId = null, CancellationToken ct = default);
    Task<AZOAResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default);

    // ─── Faucet (dev / test networks only) ───
    /// <summary>
    /// True when this provider exposes a faucet top-up path for its chain (server-
    /// side dispense like Algorand, or a client-side airdrop acknowledgement like
    /// Solana). Mirrors <see cref="SupportsBridging"/>: the chain-agnostic top-up
    /// caller checks this before dispatching, so an unsupported chain yields a clear
    /// "not supported" without inspecting concrete provider types.
    /// </summary>
    bool SupportsFaucet { get; }

    /// <summary>
    /// Dispense test funds to <paramref name="toAddress"/> on this provider's active
    /// network. Faucet operations are provider-scoped: each chain owns how (and
    /// whether) it tops up. Callers MUST enforce the mainnet guard before invoking;
    /// providers may also refuse on mainnet.
    ///
    /// <para>The on-chain submit (when server-side) is idempotent: an optional
    /// <paramref name="idempotencyKey"/> (e.g. a client <c>Idempotency-Key</c> header)
    /// dedups a retried/concurrent dispense; when absent the provider derives a
    /// deterministic content key from (chain, recipient, amount). A client-side
    /// faucet (Solana) returns <see cref="FaucetDispenseResult.IsClientSide"/> = true
    /// with a null tx hash.</para>
    /// </summary>
    Task<AZOAResult<FaucetDispenseResult>> DispenseFromFaucetAsync(
        string toAddress,
        decimal amount,
        string? idempotencyKey = null,
        CancellationToken ct = default);

    // ─── Token / Asset Lifecycle ───
    // value-path-wiring H4: the value amount is `ulong` (Algorand ASA AssetAmount
    // is ulong-native) so large allocations cannot truncate at the boundary.
    // value-path-wiring C1/D1: the optional `signingContext` carries the
    // avatar/wallet identity so the provider signs with the RIGHT key (per-user
    // custody vs platform/ASA-admin). A null context = platform/ASA-admin op.
    Task<AZOAResult<string>> MintAsync(
        string tokenUri,
        ulong amount,
        string assetType,
        string walletAddress,
        SigningContext? signingContext = null,
        CancellationToken ct = default);

    Task<AZOAResult<string>> BurnAsync(
        string tokenId,
        ulong amount,
        string walletAddress,
        SigningContext? signingContext = null,
        CancellationToken ct = default);

    Task<AZOAResult<string>> TransferAsync(
        string tokenId,
        string fromAddress,
        string toAddress,
        ulong amount,
        SigningContext? signingContext = null,
        CancellationToken ct = default);

    // ─── Exchange / Swap ───
    Task<AZOAResult<string>> ExchangeAsync(
        string sourceTokenId,
        string targetTokenId,
        string exchangeRate,
        string walletAddress,
        CancellationToken ct = default);

    Task<AZOAResult<string>> SwapAsync(
        string tokenIn,
        string tokenOut,
        decimal amountIn,
        decimal minAmountOut,
        string walletAddress,
        CancellationToken ct = default);

    // ─── Query / Metadata ───
    Task<AZOAResult<Dictionary<string, object>>> GetTokenMetadataAsync(
        string tokenId,
        CancellationToken ct = default);

    Task<AZOAResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
        string ownerAddress,
        CancellationToken ct = default);

    Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash,
        CancellationToken ct = default);

    /// <summary>
    /// Explicit confirmation tri-state for a previously-broadcast tx
    /// (blockchain-recovery-and-portable-wallets §1.2). Unlike
    /// <see cref="GetTransactionStatusAsync"/> (provider-inconsistent dictionary),
    /// this returns a normalized <see cref="ChainConfirmation"/> so callers —
    /// the reconciler and the quest engine's reconcile-before-retry hook — can
    /// safely decide advance-vs-retry-vs-wait WITHOUT re-broadcasting.
    ///
    /// <para>The base implementation derives the verdict from
    /// <see cref="GetTransactionStatusAsync"/> via <c>ChainTxClassifier</c>,
    /// preserving the conservative "IsError ⇒ Unknown, never FailedOnChain"
    /// invariant. Providers that can positively distinguish "dropped" from "not
    /// yet observed" (e.g. via a mempool lookup) SHOULD override to sharpen
    /// <see cref="ChainConfirmation.Pending"/> vs
    /// <see cref="ChainConfirmation.Unknown"/>.</para>
    /// </summary>
    Task<AZOAResult<ChainConfirmation>> GetTransactionConfirmationAsync(
        string txHash,
        CancellationToken ct = default);

    // ─── Smart Contract / Program ───
    Task<AZOAResult<string>> DeployContractAsync(
        byte[] contractCode,
        string walletAddress,
        Dictionary<string, object>? args = null,
        CancellationToken ct = default);

    Task<AZOAResult<object>> CallContractAsync(
        string contractAddress,
        string method,
        Dictionary<string, object> args,
        string walletAddress,
        CancellationToken ct = default);

    // ─── Chain Info ───
    Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(CancellationToken ct = default);

    // ─── Cross-Chain Bridge Primitives ───
    /// <summary>
    /// Lock an asset in a bridge vault on this chain (for outbound bridging).
    /// </summary>
    Task<AZOAResult<string>> LockForBridgeAsync(
        string tokenId, string vaultAddress, int amount,
        string targetChain, string targetRecipient, CancellationToken ct = default);

    /// <summary>
    /// Mint a wrapped asset representation of an asset from another chain.
    /// </summary>
    Task<AZOAResult<string>> MintWrappedAsync(
        string sourceChain, string sourceTokenId, string tokenUri,
        int amount, string recipientAddress, CancellationToken ct = default);

    /// <summary>
    /// Burn a wrapped asset to release the original asset on the source chain.
    /// </summary>
    Task<AZOAResult<string>> BurnWrappedAsync(
        string tokenId, int amount, string sourceChain,
        string sourceRecipient, string walletAddress, CancellationToken ct = default);

    /// <summary>
    /// Verify a cross-chain proof/message from another chain.
    /// </summary>
    Task<AZOAResult<bool>> VerifyBridgeProofAsync(
        string proofData, string sourceChain, string targetChainId, CancellationToken ct = default);

    /// <summary>
    /// Check if this provider supports bridging operations natively.
    /// </summary>
    bool SupportsBridging { get; }
}
