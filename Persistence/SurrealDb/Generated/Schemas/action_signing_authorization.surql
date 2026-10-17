-- ============================================================
-- Table: action_signing_authorization
-- Aggregate: Action signing authority
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS action_signing_authorization SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE action_signing_authorization TYPE option<string>;
DEFINE FIELD IF NOT EXISTS execution_id ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS run_id ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS node_id ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS expected_version ON TABLE action_signing_authorization TYPE int;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS witness_json ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS reason ON TABLE action_signing_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE action_signing_authorization TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_signing_authorization_execution
    ON TABLE action_signing_authorization
    FIELDS execution_id, created_at;
