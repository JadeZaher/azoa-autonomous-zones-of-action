namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// The thin enqueue seam the <c>Emit</c> quest node calls to fire a tenant-defined
/// <c>quest.emit</c> webhook event (final-hardening F3). Implemented by
/// <c>QuestWebhookEmitter</c>; the <c>EmitNodeHandler</c> invokes it after serializing
/// the node payload. This interface exists so the handler has a stable seam to depend on
/// without knowing the outbox/delivery machinery — the generic mirror of
/// <see cref="IConsentWebhookEmitter"/>.
///
/// <para><b>Transactional outbox (no dual-write).</b> <see cref="EmitAsync"/> ONLY writes
/// an outbox row (no outbound HTTP). A separate delivery worker
/// (<c>QuestWebhookDeliveryWorker</c>) POSTs later with retry + a timestamped per-tenant
/// HMAC.</para>
///
/// <para><b>Best-effort.</b> Emitting an event NEVER affects quest-run state. The
/// <c>Emit</c> node's serialized output remains the tenant's authoritative settlement
/// surface; a failed enqueue is logged, not thrown — the node still succeeds.</para>
/// </summary>
public interface IQuestWebhookEmitter
{
    /// <summary>
    /// Build a <c>QuestWebhookEvent</c> from the supplied run/node/quest ids, tenant,
    /// tenant-defined <paramref name="eventType"/>, and serialized
    /// <paramref name="payloadJson"/>, then enqueue it on the outbox. Returns when the
    /// row is written (NOT when it is delivered). Never throws — a plumbing failure is
    /// swallowed so the emitting node still succeeds.
    /// </summary>
    Task EmitAsync(
        Guid tenantId,
        string eventType,
        Guid runId,
        Guid nodeId,
        Guid questId,
        string payloadJson,
        CancellationToken ct = default);
}
