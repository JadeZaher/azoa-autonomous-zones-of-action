using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the consent-webhook transactional outbox
/// (tenant-consent-delegation §4, AC7). Mirrors the saga outbox seam
/// (<c>ISagaStore</c>): a row is written, a polling worker claims due rows and
/// transitions them with conditional single-winner updates.
///
/// <para><b>No dual-write (AC7).</b> <see cref="EnqueueAsync"/> is called by
/// ConsentManager IN THE SAME logical transaction as the grant/revoke state change —
/// right after the grant <c>UpsertAsync</c>. AZOA never makes an outbound HTTP call
/// inside the request that mutated the grant; the request only writes this outbox row,
/// exactly like the saga step producer.</para>
///
/// <para><b>No-throw contract.</b> Every method returns an <see cref="AZOAResult{T}"/>
/// and captures exceptions rather than throwing — the delivery worker logs +
/// reschedules on a failed transition, it does not bubble.</para>
/// </summary>
public interface IConsentWebhookOutboxStore
{
    /// <summary>
    /// CREATE the outbox row (AC7 — enqueued in the SAME logical transaction as the
    /// grant/revoke Upsert, no dual-write). The caller (ConsentManager via
    /// <c>IConsentWebhookEmitter</c>) enqueues right after the grant state change.
    /// Returns the persisted event.
    /// </summary>
    Task<AZOAResult<ConsentWebhookEvent>> EnqueueAsync(ConsentWebhookEvent evt, CancellationToken ct = default);

    /// <summary>
    /// The worker's due-scan: <c>Pending</c> rows whose <c>next_attempt_at &lt;= now</c>,
    /// oldest first, bounded by <paramref name="limit"/>. The delivery worker iterates
    /// these and attempts delivery.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<ConsentWebhookEvent>>> ListDueAsync(
        DateTime now, int limit, CancellationToken ct = default);

    /// <summary>
    /// Terminal success: conditional <c>UPDATE … WHERE status='Pending'</c> ⇒
    /// <c>Delivered</c>. Returns whether exactly one row changed (single-winner — a
    /// concurrent transition already moved it ⇒ false, caller no-ops).
    /// </summary>
    Task<AZOAResult<bool>> MarkDeliveredAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Record a failed attempt and schedule the retry: conditional on the row still
    /// being <c>Pending</c>, set <c>attempt_count = attemptCount</c>, push
    /// <c>next_attempt_at</c> to <paramref name="nextAttemptAt"/> (the exponential
    /// backoff), store <paramref name="lastError"/>. Returns whether it applied.
    /// </summary>
    Task<AZOAResult<bool>> RescheduleAsync(
        Guid id, int attemptCount, DateTime nextAttemptAt, string lastError, CancellationToken ct = default);

    /// <summary>
    /// Dead-letter a row that exhausted retries (or is permanently undeliverable — no
    /// active registration, SSRF-blocked URL): conditional on <c>Pending</c>, set
    /// <c>DeadLettered</c> + <c>last_error</c>. Terminal; never retried. Returns whether
    /// it applied.
    /// </summary>
    Task<AZOAResult<bool>> DeadLetterAsync(Guid id, string lastError, CancellationToken ct = default);
}
