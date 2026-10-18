-- ============================================================
-- Table: hold_recovery_receipt
-- Aggregate: Hold recovery audit
-- Guardrail: Immutable original command, expected version and recovery result
-- ============================================================

DEFINE TABLE IF NOT EXISTS hold_recovery_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS kind ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hold_recovery_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS original_state ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS original_version ON TABLE hold_recovery_receipt TYPE int;
DEFINE FIELD IF NOT EXISTS outcome_json ON TABLE hold_recovery_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_recovery_receipt TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hold_recovery_operation
    ON TABLE hold_recovery_receipt
    FIELDS operation_id, created_at;
