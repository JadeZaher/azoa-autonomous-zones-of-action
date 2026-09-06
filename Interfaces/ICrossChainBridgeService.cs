using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Hybrid cross-chain bridge orchestrator supporting both trusted (custodial)
/// and trustless (Wormhole) bridge modes.
/// </summary>
public interface ICrossChainBridgeService
{
    /// <summary>
    /// Initiate a bridge: lock asset on source chain and mint wrapped on target.
    /// When mode is Wormhole, the transfer pauses at AwaitingVAA until the client
    /// calls RedeemWithVAAAsync to complete trustlessly.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client-supplied idempotency key (the <c>Idempotency-Key</c>
    /// request header). The service hashes and user-namespaces it, then binds
    /// its ledger record to the exact operation and request fingerprint. When
    /// null the service derives a deterministic content key.
    /// </param>
    /// <param name="amount">
    /// The value to bridge, as arbitrary-precision base units plus the dimension they are
    /// counted in (D13/D15). Deliberately has NO default: a value-moving call must state the
    /// quantity. A non-chain dimension, or a count above the persisted 64-bit ceiling, is
    /// refused rather than reinterpreted or truncated.
    /// </param>
    Task<AZOAResult<BridgeTransactionResult>> InitiateBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid userId, RailAmount amount,
        BridgeMode? mode = null, CancellationToken ct = default,
        string? clientIdempotencyKey = null);

    /// <summary>
    /// Fetch the signed VAA for a Wormhole bridge transaction.
    /// Polls the Guardian network until the VAA is available or timeout.
    /// </summary>
    Task<AZOAResult<BridgeTransactionResult>> FetchVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        Guid? callerUserId = null);

    /// <summary>
    /// Redeem a Wormhole bridge on the target chain using a verified VAA.
    /// Completes the trustless transfer by submitting the VAA to the target chain.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client <c>Idempotency-Key</c>. The service hashes and
    /// user-namespaces it and binds it to this exact redeem request. When null
    /// the service derives a deterministic key from the bridge id and VAA digest.
    /// </param>
    Task<AZOAResult<BridgeTransactionResult>> RedeemWithVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        string? clientIdempotencyKey = null,
        Guid? callerUserId = null);

    /// <summary>
    /// Reverse bridge: burn wrapped on target, release original on source.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client <c>Idempotency-Key</c>. The service hashes and
    /// user-namespaces it and binds it to this exact reversal request. When
    /// null the service derives a deterministic bridge-and-recipient key.
    /// </param>
    Task<AZOAResult<BridgeTransactionResult>> ReverseBridgeAsync(
        string bridgeTransactionId, string sourceRecipientAddress, CancellationToken ct = default,
        string? clientIdempotencyKey = null,
        Guid? callerUserId = null);

    /// <summary>
    /// Get bridge history for an user.
    /// </summary>
    Task<AZOAResult<IEnumerable<BridgeTransactionResult>>> GetBridgeHistoryAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Get all supported bridge routes (including Wormhole availability).
    /// </summary>
    Task<AZOAResult<IEnumerable<BridgeRouteInfo>>> GetSupportedRoutesAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Get the status of a specific bridge transaction.
    /// </summary>
    Task<AZOAResult<BridgeTransactionResult>> GetBridgeStatusAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        Guid? callerUserId = null);
}
