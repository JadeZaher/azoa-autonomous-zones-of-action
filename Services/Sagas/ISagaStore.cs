using OASIS.WebAPI.Models.Sagas;

namespace OASIS.WebAPI.Sagas;

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
    /// <c>NextRunAt=now</c>, clear <c>gate_id</c>, so the processor resumes it on
    /// the next tick. A duplicate or racing signal un-parks AT MOST ONCE (the
    /// second sees the row already <c>Pending</c> and affects zero rows).
    /// Returns the un-parked record, or <c>null</c> if no parked row matched
    /// (already signalled, never parked, or wrong gate).
    /// </summary>
    Task<SagaStepRecord?> TrySignalAsync(
        string correlationKey, string gateId, CancellationToken ct);

    /// <summary>
    /// Overwrite a step's opaque <c>Payload</c> (durable-workflow-engine). Used
    /// to thread an external <c>signal</c> body onto a just-un-parked gate step
    /// so the resumed handler sees it. Unconditional (no status guard) — the
    /// caller has already won the un-park via <see cref="TrySignalAsync"/>, and
    /// the payload is opaque to the saga layer. Returns whether the row was
    /// found and written.
    /// </summary>
    Task<bool> UpdateStepPayloadAsync(Guid id, string payloadJson, CancellationToken ct);

    /// <summary>Fetch a step by id (diagnostics / tests). No tracking.</summary>
    Task<SagaStepRecord?> GetAsync(Guid id, CancellationToken ct);
}
