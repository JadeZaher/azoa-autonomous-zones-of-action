// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the saga_steps table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("saga_steps",
        Aggregate = "SagaStepRecord (Models/Sagas/SagaStepRecord.cs)",
        Guardrail = "G6 SCHEMAFULL, G2 conditional-claim single-winner primitive")]
    [SurrealNote("Durable transactional-outbox / saga-step record. One row == one step of one saga instance. The step processor only ever asks 'what is due?' and claims a row with a conditional UPDATE -- the proven api-safety-hardening single-winner primitive.")]
    [SurrealNote("G2 single-winner claim: UPDATE saga_steps:<id> SET status='InProgress', claimed_at=now WHERE status='Pending' AND next_run_at<=now RETURN AFTER. Under N concurrent processors at most one row mutates (AffectedCount==1); the rest see zero affected and back off. The conditional predicate (NOT optimistic-concurrency) is the arbiter, so behaviour is identical on Postgres, SQLite, and SurrealDB.")]
    [SurrealNote("Crash-safe re-entry: an InProgress row whose claimed_at is older than the lease/visibility timeout is reclaimed atomically (UPDATE ... SET status='Pending', claimed_at=NONE WHERE status='InProgress' AND claimed_at < lease_cutoff). Handlers key irreversible effects on step_idempotency_key (via IIdempotencyStore) so a resumed step is an idempotent replay, never a double-run.")]
    [SurrealNote("Generic by construction: no bridge/domain types appear here. saga_name/step_name are free strings and payload is opaque JSON. The bridge becomes one consumer with zero schema change.")]
    [SurrealNote("step_idempotency_key is NON-unique here -- dedup of irreversible effects happens in handlers via IIdempotencyStore, NOT via a unique constraint (the outbox legitimately holds many rows per correlation: forward steps + a compensation step + retries).")]
    [Slice("bridge")]
    [Index("saga_steps_correlation_key", Fields = new[] { "correlation_key" })]
    [Index("saga_steps_due_scan", Fields = new[] { "status", "next_run_at" })]
    [Index("saga_steps_lease_scan", Fields = new[] { "status", "claimed_at" })]
    [Index("saga_steps_idempotency_key", Fields = new[] { "step_idempotency_key" })]
    public partial class SagaSteps : ISurrealRecord
    {
        public const string SchemaNameConst = "saga_steps";
        public string SchemaName => SchemaNameConst;

        public enum StepStatus
        {
            Pending,
            InProgress,
            Completed,
            Compensating,
            DeadLettered,
            // Suspended on an external signal/timer (durable-workflow-engine).
            // Invisible to the due-step claim scan until signalled or its timer
            // fires, at which point it returns to Pending (due now).
            Parked,
        }

        [Id]
        [FieldGroup("Core identity")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Instance correlation key (NON-unique -- many rows per instance: forwards + compensation + retries)")]
        [Required(NotEmpty = true)]
        public string CorrelationKey { get; set; } = string.Empty;

        [FieldGroup("Saga definition + step name (free strings -- generic outbox)")]
        [Required(NotEmpty = true)]
        public string SagaName { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string StepName { get; set; } = string.Empty;

        [FieldGroup("Per-step idempotency key (handler keys irreversible effect on THIS value via IIdempotencyStore -- stable across retries/reclaims)")]
        [Required(NotEmpty = true)]
        public string StepIdempotencyKey { get; set; } = string.Empty;

        [FieldGroup("Opaque payload (serialized typed step input)")]
        public string Payload { get; set; } = string.Empty;

        [FieldGroup("Lifecycle state (StepStatus enum) -- drives the G2 conditional claim")]
        [Inside("Pending", "InProgress", "Completed", "Compensating", "DeadLettered", "Parked")]
        [Default("\"Pending\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StepStatus Status { get; set; }

        [FieldGroup("Compensation flag (forward steps dead-letter on exhaustion, compensation steps dead-letter immediately)")]
        [Default("false")]
        public bool IsCompensation { get; set; }

        [FieldGroup("Attempts consumed so far")]
        [Default("0")]
        public long AttemptCount { get; set; }

        [FieldGroup("Earliest UTC time the step may be claimed (pushed out by backoff on retry)")]
        public DateTimeOffset NextRunAt { get; set; }

        [FieldGroup("Lease/visibility-timeout tracking (NONE when not claimed)")]
        public DateTimeOffset? ClaimedAt { get; set; }

        [Optional]
        [FieldGroup("Diagnostics")]
        public string? LastError { get; set; }

        [Optional]
        public string? Output { get; set; }

        [FieldGroup("Mirror of status==DeadLettered for cheap querying")]
        [Default("false")]
        public bool DeadLettered { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        [Optional]
        [FieldGroup("Gate id a Parked step waits on (durable-workflow-engine). NONE unless status==Parked. SignalAsync(correlationKey, gateId) un-parks the matching row via a G2 conditional UPDATE; a timer-armed park leaves this NONE and relies on next_run_at instead.")]
        public string? GateId { get; set; }
    }
}
