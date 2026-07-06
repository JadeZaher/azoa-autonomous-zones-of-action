using AZOA.WebAPI.Models.Sagas;

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// The durable persistence seam for the saga/outbox. Storage specifics
/// (EF/Postgres now, SurrealDB later) live ENTIRELY behind this contract — the
/// processor only ever asks "what is due?" and performs atomic state
/// transitions. A SurrealDB LIVE-query implementation can replace the polling
/// EF one with zero saga/handler change (spec: engine-portable, trigger
/// swappable).
///
/// <para><b>Single-winner claim.</b> <see cref="TryClaimDueStepAsync"/> is the
/// exactly-once executor primitive: it is an atomic conditional update
/// (<c>WHERE Id==id AND Status==Pending AND NextRunAt&lt;=now</c> ⇒
/// <c>Status=InProgress</c>) that asserts exactly one row changed — identical
/// discipline to <c>ReconciliationService</c>'s
/// <c>ExecuteUpdateAsync … WHERE Status==expected</c> + assert-one-row. Under N
/// concurrent processors at most one wins a given step.</para>
/// </summary>
public interface ISagaStore
{
    /// <summary>
    /// Enqueue the first forward step of a new saga instance (transactional
    /// outbox: in real consumers this is called inside the SAME transaction as
    /// the producing state change). Returns the persisted record.
    /// </summary>
    Task<SagaStepRecord> EnqueueAsync(
        string sagaName,
        string stepName,
        string correlationKey,
        string stepIdempotencyKey,
        string payloadJson,
        bool isCompensation,
        CancellationToken ct);

    /// <summary>
    /// Ids of steps that are due to run now: <c>Status==Pending AND
    /// NextRunAt&lt;=now</c>, oldest first, bounded by <paramref name="batch"/>.
    /// Also reclaims leases: an <c>InProgress</c> row whose <c>ClaimedAt</c> is
    /// older than <paramref name="leaseTimeout"/> is a crashed processor — it
    /// is atomically returned to <c>Pending</c> (due now) before the scan so it
    /// is included. Crash-safe re-entry.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetDueStepIdsAsync(
        DateTime now, int batch, TimeSpan leaseTimeout, CancellationToken ct);

    /// <summary>
    /// Atomically claim a specific due step: conditional
    /// <c>UPDATE … WHERE Id==id AND Status==Pending AND NextRunAt&lt;=now</c>
    /// SET <c>Status=InProgress, ClaimedAt=now</c>; returns the claimed record
    /// ONLY if exactly one row changed (this caller won), else <c>null</c>
    /// (another processor won, or it is no longer due). This is the
    /// single-winner primitive.
    /// </summary>
    Task<SagaStepRecord?> TryClaimDueStepAsync(
        Guid id, DateTime now, CancellationToken ct);

    /// <summary>
    /// Mark a claimed step <see cref="StepStatus.Completed"/> (conditional on it
    /// still being <see cref="StepStatus.InProgress"/>), recording its output.
    /// Returns whether the conditional write applied (false ⇒ a concurrent
    /// reclaim/transition already moved it; the caller no-ops).
    /// </summary>
    Task<bool> CompleteStepAsync(Guid id, string? output, CancellationToken ct);

    /// <summary>
    /// Record a failed attempt and schedule the retry: conditional on the row
    /// still being <see cref="StepStatus.InProgress"/>, set it back to
    /// <see cref="StepStatus.Pending"/>, bump <c>AttemptCount</c>, push
    /// <c>NextRunAt</c> out by <paramref name="backoff"/>, clear the lease,
    /// store <paramref name="error"/>. Returns whether it applied.
    /// </summary>
    Task<bool> ScheduleRetryAsync(
        Guid id, DateTime nextRunAt, string error, CancellationToken ct);

    /// <summary>
    /// Terminal-fail a forward step that exhausted retries: conditional on
    /// <see cref="StepStatus.InProgress"/>, set <see cref="StepStatus.Compensating"/>
    /// and enqueue the declared compensation step as a fresh Pending row in the
    /// SAME unit of work. Returns the enqueued compensation record, or
    /// <c>null</c> if the transition no longer applied.
    /// </summary>
    Task<SagaStepRecord?> CompensateStepAsync(
        Guid id,
        string compensationStepName,
        string compensationIdempotencyKey,
        string compensationPayloadJson,
        string error,
        CancellationToken ct);

    /// <summary>
    /// Dead-letter a step (a forward step with NO declared compensation, or a
    /// compensation step that itself exhausted): conditional on
    /// <see cref="StepStatus.InProgress"/>, set
    /// <see cref="StepStatus.DeadLettered"/> + <c>DeadLettered=true</c>.
    /// Returns whether it applied.
    /// </summary>
    Task<bool> DeadLetterStepAsync(Guid id, string error, CancellationToken ct);

    /// <summary>
    /// Enqueue the next forward step of a saga instance after a step completed
    /// (transactional outbox continuation). Returns the new record.
    /// </summary>
    Task<SagaStepRecord> EnqueueNextStepAsync(
        string sagaName,
        string nextStepName,
        string correlationKey,
        string stepIdempotencyKey,
        string payloadJson,
        CancellationToken ct);

    /// <summary>
    /// SUSPEND a claimed step on an external signal/timer (durable-workflow-engine).
    /// Conditional on the row still being <see cref="StepStatus.InProgress"/>,
    /// set <see cref="StepStatus.Parked"/>, persist <paramref name="gateId"/>,
    /// clear the lease, and set <c>NextRunAt</c> to <paramref name="resumeAt"/>
    /// when supplied (a wait/timer node fires through the existing
    /// <see cref="GetDueStepIdsAsync"/> scan) or far into the future when
    /// <c>null</c> (parks indefinitely until signalled — the gate scan never
    /// claims a <c>Parked</c> row). Returns whether the conditional write
    /// applied (false ⇒ a concurrent reclaim/transition already moved it).
    /// </summary>
    Task<bool> ParkStepAsync(
        Guid id, string gateId, DateTime? resumeAt, CancellationToken ct);

    /// <summary>
    /// Deliver an external signal to a PARKED gate step (durable-workflow-engine).
    /// G2 single-winner conditional UPDATE: <c>WHERE status==Parked AND
    /// correlation_key==$c AND gate_id==$g</c> ⇒ <see cref="StepStatus.Pending"/>,
    /// <c>NextRunAt=now</c>, clear <c>gate_id</c>, AND (when
    /// <paramref name="newPayloadJson"/> is non-null) overwrite the step payload
    /// — all in ONE atomic statement, so the processor never observes a due row
    /// whose payload still lacks the signal body (no un-park/stamp race). A
    /// duplicate or racing signal un-parks AT MOST ONCE (the second sees the row
    /// already <c>Pending</c> and affects zero rows). Returns the un-parked
    /// record, or <c>null</c> if no parked row matched (already signalled, never
    /// parked, or wrong gate).
    /// </summary>
    Task<SagaStepRecord?> TrySignalAsync(
        string correlationKey, string gateId, string? newPayloadJson, CancellationToken ct);

    /// <summary>
    /// Read (no mutation) the PARKED step matching <paramref name="correlationKey"/>
    /// + <paramref name="gateId"/>, or <c>null</c> if none is parked on that gate
    /// (durable-workflow-engine). Used to derive the signal-stamped payload
    /// BEFORE the atomic un-park, so the stamp can be folded into
    /// <see cref="TrySignalAsync"/>.
    /// </summary>
    Task<SagaStepRecord?> GetParkedStepAsync(
        string correlationKey, string gateId, CancellationToken ct);

    /// <summary>
    /// Whether a step named <paramref name="stepName"/> already exists for the
    /// saga instance <paramref name="correlationKey"/> (any status). Used to make
    /// a self-advancing consumer's downstream enqueue IDEMPOTENT: a replayed
    /// advance (the producing step re-dispatched after a crash) must not CREATE a
    /// duplicate successor row, since <c>step_idempotency_key</c> is deliberately
    /// non-unique. The producing consumer checks this before
    /// <see cref="EnqueueNextStepAsync"/>.
    /// </summary>
    Task<bool> StepExistsAsync(string correlationKey, string stepName, CancellationToken ct);

    /// <summary>Fetch a step by id (diagnostics / tests). No tracking.</summary>
    Task<SagaStepRecord?> GetAsync(Guid id, CancellationToken ct);

    // ── Operator / dead-letter surface (Phase-F) ─────────────────────────────

    /// <summary>
    /// List steps in any of the supplied <paramref name="statuses"/> (operator
    /// dead-letter inspection), newest-updated first, bounded by
    /// <paramref name="limit"/>. Read-only; carries enough of each record
    /// (saga/correlation/step/status/attempt/last_error) for an operator to
    /// diagnose. The generic saga-step primitive beneath the quest-specific
    /// reconcile board — see Services/Sagas/AGENTS.md §operator-surface.
    /// </summary>
    Task<IReadOnlyList<SagaStepRecord>> ListByStatusesAsync(
        IReadOnlyCollection<StepStatus> statuses, int limit, CancellationToken ct);

    /// <summary>
    /// Operator REQUEUE: reset a <see cref="StepStatus.Parked"/> or
    /// <see cref="StepStatus.DeadLettered"/> step back to
    /// <see cref="StepStatus.Pending"/> (due now, lease + gate cleared) so the
    /// processor picks it up again. Conditional on the row currently being one of
    /// those two revivable statuses, so it is idempotent (a second requeue of an
    /// already-Pending / already-running row affects zero rows and returns
    /// <c>false</c>) and REFUSES to revive a terminally
    /// <see cref="StepStatus.Cancelled"/> or already-<see cref="StepStatus.Completed"/>
    /// step. Returns whether the conditional write applied.
    /// </summary>
    Task<bool> RequeueStepAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Operator CANCEL: terminally mark a <see cref="StepStatus.Parked"/>,
    /// <see cref="StepStatus.DeadLettered"/>, or still-<see cref="StepStatus.Pending"/>
    /// step <see cref="StepStatus.Cancelled"/> so it never retries. Conditional on
    /// the row being in one of those non-terminal-or-dead states (an InProgress
    /// step is being actively worked and is left to the lease; a Completed step is
    /// done) — idempotent, and it will not un-cancel or un-complete. Returns
    /// whether the conditional write applied.
    /// </summary>
    Task<bool> CancelStepAsync(Guid id, string reason, CancellationToken ct);
}
