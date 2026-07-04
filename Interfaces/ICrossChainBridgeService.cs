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
    /// request header). When provided it is used VERBATIM as the lock→mint
    /// idempotency key so a retried initiate collapses to one irreversible
    /// chain effect. When null the service derives a deterministic content key
    /// from (avatar, route, token, recipient, amount) — absence is still
    /// dedup-safe (no random per-request key is ever generated).
    /// </param>
    Task<AZOAResult<BridgeTransactionResult>> InitiateBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, int amount = 1,
        BridgeMode? mode = null, CancellationToken ct = default,
        string? clientIdempotencyKey = null);

    /// <summary>
    /// Complete a bridge by confirming the target-chain mint (trusted mode).
    /// <paramref name="callerAvatarId"/>: when set, the row must belong to that
    /// avatar (IDOR guard at the untrusted boundary; internal callers pass null).
    /// </summary>
    Task<AZOAResult<BridgeTransactionResult>> CompleteBridgeAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        Guid? callerAvatarId = null);

    /// <summary>
    /// Fetch the signed VAA for a Wormhole bridge transaction.
    /// Polls the Guardian network until the VAA is available or timeout.
    /// </summary>
    Task<AZOAResult<BridgeTransactionResult>> FetchVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        Guid? callerAvatarId = null);

    /// <summary>
    /// Redeem a Wormhole bridge on the target chain using a verified VAA.
    /// Completes the trustless transfer by submitting the VAA to the target chain.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client <c>Idempotency-Key</c>. When provided it is used verbatim
    /// as the redeem idempotency key; when null the service derives a
    /// deterministic key from (bridge id, VAA digest) — absence is dedup-safe.
    /// </param>
    Task<AZOAResult<BridgeTransactionResult>> RedeemWithVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        string? clientIdempotencyKey = null,
        Guid? callerAvatarId = null);

    /// <summary>
    /// Reverse bridge: burn wrapped on target, release original on source.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client <c>Idempotency-Key</c>. When provided it is used verbatim
    /// as the reverse idempotency key; when null the service derives a
    /// deterministic key from (bridge id, source recipient) — absence is
    /// dedup-safe.
    /// </param>
    Task<AZOAResult<BridgeTransactionResult>> ReverseBridgeAsync(
        string bridgeTransactionId, string sourceRecipientAddress, CancellationToken ct = default,
        string? clientIdempotencyKey = null,
        Guid? callerAvatarId = null);

    /// <summary>
    /// Get bridge history for an avatar.
    /// </summary>
    Task<AZOAResult<IEnumerable<BridgeTransactionResult>>> GetBridgeHistoryAsync(
        Guid avatarId, CancellationToken ct = default);

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
        Guid? callerAvatarId = null);
}
