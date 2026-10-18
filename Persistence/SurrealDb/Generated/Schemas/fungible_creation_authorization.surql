-- ============================================================
-- Table: fungible_creation_authorization
-- Aggregate: Fungible creation
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS fungible_creation_authorization SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE fungible_creation_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE fungible_creation_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE fungible_creation_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fungible_creation_authorization TYPE option<string>;
DEFINE FIELD IF NOT EXISTS expected_version ON TABLE fungible_creation_authorization TYPE int;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE fungible_creation_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE fungible_creation_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE fungible_creation_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS prepared_hash ON TABLE fungible_creation_authorization TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fungible_creation_authorization TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fungible_creation_authorization_operation
    ON TABLE fungible_creation_authorization
    FIELDS operation_id, created_at;
