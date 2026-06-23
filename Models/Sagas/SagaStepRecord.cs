using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.Models.Sagas;

/// <summary>
/// The durable transactional-outbox / saga-step record. One row == one step of
/// one saga instance. Written in the SAME storage transaction as the state
/// change that produced it (transactional outbox: no dual-write, no broker
/// needed for atomicity). The step processor only ever asks the store "what is
/// due?" and claims a row with a conditional UPDATE — exactly the proven
/// api-safety-hardening single-winner primitive.
///
/// <para><b>Storage backend.</b> Persisted in the SurrealDB <c>saga_steps</c>
/// table (POCO: <c>Persistence/SurrealDb/Models/SagaSteps.cs</c>;
/// committed DDL: <c>Persistence/SurrealDb/Generated/Schemas/saga_steps.surql</c>).
/// The mapping between this POCO and the SurrealDB row lives in
/// <see cref="AZOA.WebAPI.Sagas.SurrealSagaStore"/>.</para>
///
/// <para><b>Generic by construction.</b> No bridge (or any domain) type appears
/// here — <see cref="SagaName"/>/<see cref="StepName"/> are free strings and
/// <see cref="Payload"/> is opaque JSON. The bridge becomes one consumer in a
/// later phase with zero schema change.</para>
/// </summary>
public class SagaStepRecord
{
    /// <summary>Surrogate primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Stable instance correlation key for the whole saga run (reuses the
    /// api-safety-hardening key convention). Every step record of one saga
    /// instance shares this. NON-unique index — dedup of irreversible effects
    /// happens in handlers via <see cref="AZOA.WebAPI.Interfaces.IIdempotencyStore"/>,
    /// NOT via a unique constraint here (the outbox legitimately holds many
    /// rows per correlation: forward steps + a compensation step + retries).
    /// </summary>
    public string CorrelationKey { get; set; } = string.Empty;

    /// <summary>Registered saga definition name.</summary>
    public string SagaName { get; set; } = string.Empty;

    /// <summary>The step's name within the definition (forward or compensation).</summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>
    /// Stable per-step idempotency key. The handler keys its irreversible
    /// effect on THIS value via the shared idempotency store. Stable across
    /// every retry/reclaim of this step ⇒ a re-run is an idempotent replay,
    /// never a duplicate effect.
    /// </summary>
    public string StepIdempotencyKey { get; set; } = string.Empty;

    /// <summary>Opaque JSON payload (serialized typed step input).</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Step lifecycle state — drives the conditional claim.</summary>
    public StepStatus Status { get; set; } = StepStatus.Pending;

    /// <summary><c>true</c> when this row is a declared compensation step
    /// (so the processor dead-letters it on exhaustion instead of compensating
    /// again).</summary>
    public bool IsCompensation { get; set; }

    /// <summary>Attempts consumed so far for this step.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Earliest UTC time the step may be claimed. Set forward by backoff on
    /// retry; the conditional claim is <c>WHERE Status==Pending AND
    /// NextRunAt&lt;=now</c>.
    /// </summary>
    public DateTime NextRunAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the row was atomically claimed (Status→InProgress). The
    /// lease/visibility timeout reclaim treats an InProgress row whose
    /// <see cref="ClaimedAt"/> is older than the lease as a crashed processor
    /// and makes it due again — crash-safe re-entry.
    /// </summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>Last failure message (diagnostics / dead-letter triage).</summary>
    public string? LastError { get; set; }

    /// <summary>Last successful step output (observability / next-step seed).</summary>
    public string? Output { get; set; }

    /// <summary><c>true</c> once the step has been parked in the dead-letter
    /// queue (terminal, operator-resolved). Mirrors <see cref="Status"/> ==
    /// <see cref="StepStatus.DeadLettered"/> for cheap querying.</summary>
    public bool DeadLettered { get; set; }

    /// <summary>
    /// The gate id a <see cref="StepStatus.Parked"/> step is suspended on
    /// (durable-workflow-engine). <c>null</c> unless the step is parked on an
    /// external signal. <c>SignalAsync(correlationKey, gateId, …)</c> un-parks
    /// the matching row via a G2 single-winner conditional UPDATE. A timer-armed
    /// park (wait node) leaves this <c>null</c> and relies on
    /// <see cref="NextRunAt"/> instead, firing through the existing due scan.
    /// </summary>
    public string? GateId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
