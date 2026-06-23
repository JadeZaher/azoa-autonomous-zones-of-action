using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// The thin enqueue seam ConsentManager calls to emit a consent webhook event
/// (tenant-consent-delegation §4, AC7). Implemented by <c>ConsentWebhookEmitter</c>;
/// the ORCHESTRATOR wires the actual call from ConsentManager (after a grant/revoke
/// state change) — this interface exists so that call site has a stable seam to depend
/// on without ConsentManager knowing the outbox/delivery machinery.
///
/// <para><b>Transactional outbox (AC7 — no dual-write).</b> <see cref="EmitAsync"/> ONLY
/// writes an outbox row (it does NOT make an outbound HTTP call). ConsentManager invokes
/// it in the SAME logical transaction as the grant/revoke Upsert — the event and the
/// state change land together; a separate delivery worker POSTs later. This is the saga
/// outbox discipline applied to consent events.</para>
///
/// <para><b>Observe-only (AC8).</b> Emitting an event NEVER affects consent validity —
/// the signing seam is the enforcement path. A revoked grant is dead the instant
/// <c>RevokedAt</c> is written, regardless of whether this emit (or its later delivery)
/// succeeds.</para>
/// </summary>
public interface IConsentWebhookEmitter
{
    /// <summary>
    /// Build a <see cref="ConsentWebhookEvent"/> from <paramref name="grant"/> (tenantId,
    /// grantId, avatarId = grantor, scopes, participationRef, a fresh idempotency id) for
    /// the given <paramref name="type"/> and enqueue it on the outbox. Returns when the
    /// row is written (NOT when it is delivered).
    /// </summary>
    Task EmitAsync(ConsentWebhookEventType type, ConsentGrant grant, CancellationToken ct = default);
}
