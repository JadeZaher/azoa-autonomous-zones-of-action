using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// A value rail that is a blockchain: everything on <see cref="IValueRailProvider"/>
/// plus the chain-only surface — contract deploy/call, wrapped mint/burn, faucets,
/// token metadata, and capability modules (decision D9).
/// </summary>
/// <remarks>
/// A caller that only moves value should depend on <see cref="IValueRailProvider"/>;
/// depending on this interface asserts the counterparty is a chain, which a fiat rail
/// is not. See <c>Interfaces/AGENTS.md</c> §value-rail.
/// </remarks>
public interface IBlockchainProvider : IValueRailProvider
{
    string ChainType { get; }
    ChainNetwork ActiveNetwork { get; }

    /// <summary>
    /// Initializes one previously unbound provider instance for a single network.
    /// Callers should obtain production providers from
    /// <c>IBlockchainProviderFactory</c>; factory-bound providers reject external
    /// reinitialization so a cached network binding cannot be retargeted.
    /// <para>The chain-shaped form of <see cref="IValueRailProvider.InitializeRail"/>.</para>
    /// </summary>
    void Initialize(BlockchainNetworkConfig config, ChainNetwork network);

    /// <summary>
    /// Get a chain-specific capability module (e.g., IAlgorandASAModule, ISolanaMetaplexModule).
    /// </summary>
    bool TryGetModule<T>(out T? module) where T : class, IBlockchainProviderModule;

    // ─── Faucet (dev / test networks only) ───
    /// <summary>
    /// True when this provider exposes a faucet top-up path for its chain (server-
    /// side dispense like Algorand, or a client-side airdrop acknowledgement like
    /// Solana). The chain-agnostic top-up caller checks this before dispatching, so
    /// an unsupported chain yields a clear "not supported" without inspecting
    /// concrete provider types.
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
    // D13/D15: the value amount is a RailAmount — arbitrary-precision base units plus
    // the dimension they are counted in. It replaced `ulong`, which was chosen because
    // Algorand's ASA AssetAmount is ulong-native but capped the whole interface at
    // ~18.45 ETH. Chains whose SDK takes 64 bits narrow at their own edge and REFUSE
    // rather than truncate; see RailAmount.TryToUInt64.
    // value-path-wiring C1/D1: the optional `signingContext` carries the
    // user/wallet identity so the provider signs with the RIGHT key (per-user
    // custody vs platform/ASA-admin). A null context = platform/ASA-admin op.
    Task<AZOAResult<string>> MintAsync(
        string tokenUri,
        RailAmount amount,
        string assetType,
        string walletAddress,
        SigningContext? signingContext = null,
        CancellationToken ct = default);

    Task<AZOAResult<string>> BurnAsync(
        string tokenId,
        RailAmount amount,
        string walletAddress,
        SigningContext? signingContext = null,
        CancellationToken ct = default);

    // TransferAsync is inherited from IValueRailProvider: moving value is the one thing
    // every rail does, so it is declared once, there.

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

    // GetTransactionStatusAsync / GetTransactionConfirmationAsync are inherited from
    // IValueRailProvider: "what became of this operation" is rail-generic. The base
    // provider derives the confirmation tri-state from the status dictionary via
    // ChainTxClassifier, preserving "IsError ⇒ Unknown, never FailedOnChain"; a
    // provider that can positively distinguish dropped from not-yet-observed SHOULD
    // override to sharpen Pending vs Unknown.

    // ─── Smart Contract / Program ───
    // Native mutations use INativeOperationModule; reads use IReadOnlyContractModule.

    // ─── Chain Info ───
    Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(CancellationToken ct = default);

    // ─── Cross-Chain Bridge ───
    // The bridge primitives are NOT on this interface. Lock/release live on
    // IBridgeSourceLeg and wrapped mint/burn on IBridgeTargetLeg, both resolved at
    // runtime through TryGetModule. A chain that cannot bridge does not implement the
    // module, so absence IS the fail-closed refusal — there is no `SupportsBridging`
    // boolean to set true by mistake and no refusal body that can regress into an `Ok`.
    // The direction asymmetry a single boolean could not express (Algorand is a proven
    // SOURCE but not a target) is expressed exactly by which modules a provider
    // implements. See Providers/Blockchain/AGENTS.md §bridge.
    //
    // Cross-chain proof/VAA verification is likewise NOT a provider method: it belongs
    // to IBridgeProtocol<TProof>, whose Wormhole implementation is the single hardened
    // fail-closed guardian-quorum path. A provider-level verifier previously returned
    // `true` unconditionally; it was removed so no always-true verifier can exist.
}
