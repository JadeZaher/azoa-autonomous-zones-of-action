-- ============================================================
-- Table: saga_steps
-- Aggregate: SagaStepRecord (Models/Sagas/SagaStepRecord.cs)
-- Guardrail: G6 SCHEMAFULL, G2 conditional-claim single-winner primitive
-- Note: Durable transactional-outbox / saga-step record. One row == one step of one saga instance. The step processor only ever asks 'what is due?' and claims a row with a conditional UPDATE -- the proven api-safety-hardening single-winner primitive.
-- Note: G2 single-winner claim: UPDATE saga_steps:<id> SET status='InProgress', claimed_at=now WHERE status='Pending' AND next_run_at<=now RETURN AFTER. Under N concurrent processors at most one row mutates (AffectedCount==1); the rest see zero affected and back off. The conditional predicate (NOT optimistic-concurrency) is the arbiter, so behaviour is identical on Postgres, SQLite, and SurrealDB.
-- Note: Crash-safe re-entry: an InProgress row whose claimed_at is older than the lease/visibility timeout is reclaimed atomically (UPDATE ... SET status='Pending', claimed_at=NONE WHERE status='InProgress' AND claimed_at < lease_cutoff). Handlers key irreversible effects on step_idempotency_key (via IIdempotencyStore) so a resumed step is an idempotent replay, never a double-run.
-- Note: Generic by construction: no bridge/domain types appear here. saga_name/step_name are free strings and payload is opaque JSON. The bridge becomes one consumer with zero schema change.
-- Note: step_idempotency_key is NON-unique here -- dedup of irreversible effects happens in handlers via IIdempotencyStore, NOT via a unique constraint (the outbox legitimately holds many rows per correlation: forward steps + a compensation step + retries).
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- SagaSteps.StepStatus
DEFINE PARAM IF NOT EXISTS $saga_steps_status VALUE ["Pending", "InProgress", "Completed", "Compensating", "DeadLettered", "Parked", "Cancelled"];

DEFINE TABLE IF NOT EXISTS saga_steps SCHEMAFULL;

-- Core identity
DEFINE FIELD IF NOT EXISTS id ON TABLE saga_steps TYPE string
    ASSERT $value != NONE AND $value != "";

-- Instance correlation key (NON-unique -- many rows per instance: forwards + compensation + retries)
DEFINE FIELD IF NOT EXISTS correlation_key ON TABLE saga_steps TYPE string
    ASSERT $value != NONE AND $value != "";

-- Saga definition + step name (free strings -- generic outbox)
DEFINE FIELD IF NOT EXISTS saga_name ON TABLE saga_steps TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS step_name ON TABLE saga_steps TYPE string
    ASSERT $value != NONE AND $value != "";

-- Per-step idempotency key (handler keys irreversible effect on THIS value via IIdempotencyStore -- stable across retries/reclaims)
DEFINE FIELD IF NOT EXISTS step_idempotency_key ON TABLE saga_steps TYPE string
    ASSERT $value != NONE AND $value != "";

-- Opaque payload (serialized typed step input)
DEFINE FIELD IF NOT EXISTS payload ON TABLE saga_steps TYPE string;

-- Lifecycle state (StepStatus enum) -- drives the G2 conditional claim
DEFINE FIELD IF NOT EXISTS status ON TABLE saga_steps TYPE string
    ASSERT $value INSIDE $saga_steps_status
    DEFAULT "Pending";

-- Compensation flag (forward steps dead-letter on exhaustion, compensation steps dead-letter immediately)
DEFINE FIELD IF NOT EXISTS is_compensation ON TABLE saga_steps TYPE bool
    DEFAULT false;

-- Attempts consumed so far
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE saga_steps TYPE int
    DEFAULT 0;

-- Earliest UTC time the step may be claimed (pushed out by backoff on retry)
DEFINE FIELD IF NOT EXISTS next_run_at ON TABLE saga_steps TYPE datetime;

-- Lease/visibility-timeout tracking (NONE when not claimed)
DEFINE FIELD IF NOT EXISTS claimed_at ON TABLE saga_steps TYPE option<datetime>;

-- Diagnostics
DEFINE FIELD IF NOT EXISTS last_error ON TABLE saga_steps TYPE option<string>;
DEFINE FIELD IF NOT EXISTS output ON TABLE saga_steps TYPE option<string>;

-- Mirror of status==DeadLettered for cheap querying
DEFINE FIELD IF NOT EXISTS dead_lettered ON TABLE saga_steps TYPE bool
    DEFAULT false;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE saga_steps TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE saga_steps TYPE datetime;

-- Gate id a Parked step waits on (durable-workflow-engine). NONE unless status==Parked. SignalAsync(correlationKey, gateId) un-parks the matching row via a G2 conditional UPDATE; a timer-armed park leaves this NONE and relies on next_run_at instead.
DEFINE FIELD IF NOT EXISTS gate_id ON TABLE saga_steps TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS saga_steps_correlation_key
    ON TABLE saga_steps
    FIELDS correlation_key;

DEFINE INDEX IF NOT EXISTS saga_steps_due_scan
    ON TABLE saga_steps
    FIELDS status, next_run_at;

DEFINE INDEX IF NOT EXISTS saga_steps_lease_scan
    ON TABLE saga_steps
    FIELDS status, claimed_at;

DEFINE INDEX IF NOT EXISTS saga_steps_idempotency_key
    ON TABLE saga_steps
    FIELDS step_idempotency_key;
