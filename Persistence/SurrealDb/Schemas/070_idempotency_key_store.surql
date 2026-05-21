-- ============================================================
-- Table: idempotency_key_store
-- Aggregate: IdempotencyRecord (Models/Idempotency/IdempotencyRecord.cs)
-- Guardrail: G6 SCHEMAFULL, G2 unique key constraint (insert-wins claim)
-- Note: Exactly-once execution of irreversible ops. First InProgress insert wins; concurrent inserts fail UNIQUE and read back the existing row.
-- Note: B3 review: idempotency_key_unique UNIQUE on `key` is NOT NULL (ASSERT != NONE), so SurrealDB NULL-collision semantics are not in play -- correct dedup behaviour by construction.
-- ============================================================

DEFINE TABLE idempotency_key_store SCHEMAFULL;

DEFINE FIELD id ON TABLE idempotency_key_store TYPE string
    ASSERT $value != NONE AND $value != "";

-- Idempotency key (caller-supplied or content hash); primary dedup key
DEFINE FIELD key ON TABLE idempotency_key_store TYPE string
    ASSERT $value != NONE AND $value != "";

-- Logical operation type (e.g. bridge_redeem, faucet_dispense)
DEFINE FIELD operation_type ON TABLE idempotency_key_store TYPE string
    ASSERT $value != NONE AND $value != "";

-- Lifecycle state
DEFINE FIELD state ON TABLE idempotency_key_store TYPE string
    ASSERT $value INSIDE ["InProgress", "Completed", "Failed"]
    DEFAULT "InProgress";

-- Serialized result of completed operation (replayed verbatim to duplicates)
DEFINE FIELD result_payload ON TABLE idempotency_key_store TYPE option<string>;

-- Failure reason when state == Failed
DEFINE FIELD error ON TABLE idempotency_key_store TYPE option<string>;

-- Timestamps
DEFINE FIELD created_at ON TABLE idempotency_key_store TYPE datetime;
DEFINE FIELD updated_at ON TABLE idempotency_key_store TYPE datetime;

-- TTL expiry timestamp -- sweep job purges old InProgress rows
DEFINE FIELD ttl_expires_at ON TABLE idempotency_key_store TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX idempotency_key_unique
    ON TABLE idempotency_key_store
    FIELDS key
    UNIQUE;

DEFINE INDEX idempotency_ttl_expires_at
    ON TABLE idempotency_key_store
    FIELDS ttl_expires_at;

DEFINE INDEX idempotency_operation_type
    ON TABLE idempotency_key_store
    FIELDS operation_type;
