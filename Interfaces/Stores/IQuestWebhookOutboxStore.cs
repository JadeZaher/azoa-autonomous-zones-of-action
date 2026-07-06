using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for the GENERIC quest-webhook transactional outbox
/// (final-hardening F3 — generalizes <see cref="IConsentWebhookOutboxStore"/> to
/// arbitrary <c>quest.emit</c> events). Same SHAPE as the consent outbox + the saga
/// outbox (<c>ISagaStore</c>): a row is written, a polling worker claims due rows and
/// transitions them with conditional single-winner updates.
///
/// <para><b>No dual-write.</b> <see cref="EnqueueAsync"/> is called by the <c>Emit</c>
/// node handler in the same logical write as the node's execution output. AZOA never
/// makes an outbound HTTP call inside the quest-run request; the run only writes this
/// outbox row, exactly like the consent outbox producer.</para>
///
/// <para><b>No-throw contract.</b> Every method returns an <see cref="AZOAResult{T}"/>
/// and captures exceptions rather than throwing — the delivery worker logs +
/// reschedules on a failed transition, it does not bubble.</para>
/// </summary>
public interface IQuestWebhookOutboxStore
{
    /// <summary>
    /// CREATE the outbox row (enqueued alongside the <c>Emit</c> node output, no
    /// dual-write). Returns the persisted event.
    /// </summary>
    Task<AZOAResult<QuestWebhookEvent>> EnqueueAsync(QuestWebhookEvent evt, CancellationToken ct = default);

    /// <summary>
    /// The worker's due-scan: <c>Pending</c> rows whose <c>next_attempt_at &lt;= now</c>,
    /// oldest first, bounded by <paramref name="limit"/>.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<QuestWebhookEvent>>> ListDueAsync(
        DateTime now, int limit, CancellationToken ct = default);

    /// <summary>
    /// Terminal success: conditional <c>UPDATE … WHERE status='Pending'</c> ⇒
    /// <c>Delivered</c>. Returns whether exactly one row changed (single-winner).
    /// </summary>
    Task<AZOAResult<bool>> MarkDeliveredAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Record a failed attempt and schedule the retry: conditional on the row still
    /// being <c>Pending</c>, set <c>attempt_count</c>, push <c>next_attempt_at</c> to
    /// <paramref name="nextAttemptAt"/>, store <paramref name="lastError"/>.
    /// </summary>
    Task<AZOAResult<bool>> RescheduleAsync(
        Guid id, int attemptCount, DateTime nextAttemptAt, string lastError, CancellationToken ct = default);

    /// <summary>
    /// Dead-letter a row that exhausted retries (or is permanently undeliverable):
    /// conditional on <c>Pending</c>, set <c>DeadLettered</c> + <c>last_error</c>.
    /// Terminal; never retried.
    /// </summary>
    Task<AZOAResult<bool>> DeadLetterAsync(Guid id, string lastError, CancellationToken ct = default);
}
