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

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [FieldGroup("Instance correlation key (NON-unique -- many rows per instance: forwards + compensation + retries)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("correlation_key")]
        public string CorrelationKey { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [FieldGroup("Saga definition + step name (free strings -- generic outbox)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("saga_name")]
        public string SagaName { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("step_name")]
        public string StepName { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [FieldGroup("Per-step idempotency key (handler keys irreversible effect on THIS value via IIdempotencyStore -- stable across retries/reclaims)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("step_idempotency_key")]
        public string StepIdempotencyKey { get; set; } = string.Empty;

        [Column(Order = 6, Type = "string")]
        [FieldGroup("Opaque payload (serialized typed step input)")]
        [JsonPropertyName("payload")]
        public string Payload { get; set; } = string.Empty;

        [Column(Order = 7, Type = "string")]
        [FieldGroup("Lifecycle state (StepStatus enum) -- drives the G2 conditional claim")]
        [Inside("Pending", "InProgress", "Completed", "Compensating", "DeadLettered", "Parked")]
        [Default("\"Pending\"")]
        [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
        public StepStatus Status { get; set; }

        [Column(Order = 8, Type = "bool")]
        [FieldGroup("Compensation flag (forward steps dead-letter on exhaustion, compensation steps dead-letter immediately)")]
        [Default("false")]
        [JsonPropertyName("is_compensation")]
        public bool IsCompensation { get; set; }

        [Column(Order = 9, Type = "int")]
        [FieldGroup("Attempts consumed so far")]
        [Default("0")]
        [JsonPropertyName("attempt_count")]
        public long AttemptCount { get; set; }

        [Column(Order = 10, Type = "datetime")]
        [FieldGroup("Earliest UTC time the step may be claimed (pushed out by backoff on retry)")]
        [JsonPropertyName("next_run_at")]
        public DateTimeOffset NextRunAt { get; set; }

        [Column(Order = 11, Type = "option<datetime>")]
        [FieldGroup("Lease/visibility-timeout tracking (NONE when not claimed)")]
        [JsonPropertyName("claimed_at")]
        public DateTimeOffset? ClaimedAt { get; set; }

        [Column(Order = 12, Type = "option<string>")]
        [FieldGroup("Diagnostics")]
        [JsonPropertyName("last_error")]
        public string? LastError { get; set; }

        [Column(Order = 13, Type = "option<string>")]
        [JsonPropertyName("output")]
        public string? Output { get; set; }

        [Column(Order = 14, Type = "bool")]
        [FieldGroup("Mirror of status==DeadLettered for cheap querying")]
        [Default("false")]
        [JsonPropertyName("dead_lettered")]
        public bool DeadLettered { get; set; }

        [Column(Order = 15, Type = "datetime")]
        [FieldGroup("Timestamps")]
        [ReadOnly]
        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 16, Type = "datetime")]
        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [Column(Order = 17, Type = "option<string>")]
        [FieldGroup("Gate id a Parked step waits on (durable-workflow-engine). NONE unless status==Parked. SignalAsync(correlationKey, gateId) un-parks the matching row via a G2 conditional UPDATE; a timer-armed park leaves this NONE and relies on next_run_at instead.")]
        [JsonPropertyName("gate_id")]
        public string? GateId { get; set; }
    }
}
