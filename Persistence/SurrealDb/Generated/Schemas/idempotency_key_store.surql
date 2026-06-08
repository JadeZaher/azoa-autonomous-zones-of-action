-- ============================================================
-- Table: idempotency_key_store
-- Aggregate: IdempotencyRecord (Models/Idempotency/IdempotencyRecord.cs)
-- Guardrail: G6 SCHEMAFULL, G2 unique key constraint (insert-wins claim)
-- Note: Exactly-once execution of irreversible ops. First InProgress insert wins; concurrent inserts fail UNIQUE and read back the existing row.
-- Note: B3 review: idempotency_key_unique UNIQUE on `key` is NOT NULL (ASSERT != NONE), so SurrealDB NULL-collision semantics are not in play -- correct dedup behaviour by construction.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- IdempotencyKeyStore.StateKind
DEFINE PARAM IF NOT EXISTS $idempotency_key_store_state VALUE ["InProgress", "Completed", "Failed"];

DEFINE TABLE IF NOT EXISTS idempotency_key_store SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE idempotency_key_store TYPE string
    ASSERT $value != NONE AND $value != "";

-- Idempotency key (caller-supplied or content hash); primary dedup key
DEFINE FIELD IF NOT EXISTS key ON TABLE idempotency_key_store TYPE string
    ASSERT $value != NONE AND $value != "";

-- Logical operation type (e.g. bridge_redeem, faucet_dispense)
DEFINE FIELD IF NOT EXISTS operation_type ON TABLE idempotency_key_store TYPE string
    ASSERT $value != NONE AND $value != "";

-- Lifecycle state
DEFINE FIELD IF NOT EXISTS state ON TABLE idempotency_key_store TYPE string
    ASSERT $value INSIDE $idempotency_key_store_state
    DEFAULT "InProgress";

-- Serialized result of completed operation (replayed verbatim to duplicates)
DEFINE FIELD IF NOT EXISTS result_payload ON TABLE idempotency_key_store TYPE option<string>;

-- Failure reason when state == Failed
DEFINE FIELD IF NOT EXISTS error ON TABLE idempotency_key_store TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE idempotency_key_store TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE idempotency_key_store TYPE datetime;

-- TTL expiry timestamp -- sweep job purges old InProgress rows
DEFINE FIELD IF NOT EXISTS ttl_expires_at ON TABLE idempotency_key_store TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS idempotency_key_unique
    ON TABLE idempotency_key_store
    FIELDS key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS idempotency_ttl_expires_at
    ON TABLE idempotency_key_store
    FIELDS ttl_expires_at;

DEFINE INDEX IF NOT EXISTS idempotency_operation_type
    ON TABLE idempotency_key_store
    FIELDS operation_type;
