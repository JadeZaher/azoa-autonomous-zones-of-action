-- ============================================================
-- Table: fungible_creation_cancellation
-- Aggregate: Fungible creation
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS fungible_creation_cancellation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE fungible_creation_cancellation TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE fungible_creation_cancellation TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE fungible_creation_cancellation TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fungible_creation_cancellation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE fungible_creation_cancellation TYPE string;
DEFINE FIELD IF NOT EXISTS credential_json ON TABLE fungible_creation_cancellation TYPE string;
DEFINE FIELD IF NOT EXISTS expected_version ON TABLE fungible_creation_cancellation TYPE int;
DEFINE FIELD IF NOT EXISTS generation ON TABLE fungible_creation_cancellation TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fungible_creation_cancellation TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fungible_creation_cancel_operation
    ON TABLE fungible_creation_cancellation
    FIELDS operation_id
    UNIQUE;
