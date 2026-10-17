-- ============================================================
-- Table: hold_command_authorization
-- Aggregate: Exact hold continuation consent
-- Guardrail: Immutable original operation, version, quote and authority receipt
-- ============================================================

DEFINE TABLE IF NOT EXISTS hold_command_authorization SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS kind ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hold_command_authorization TYPE option<string>;
DEFINE FIELD IF NOT EXISTS expected_version ON TABLE hold_command_authorization TYPE int;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS quote_id ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS maximum_network_fee_base_units ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE hold_command_authorization TYPE string;
DEFINE FIELD IF NOT EXISTS prepared_hash ON TABLE hold_command_authorization TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_command_authorization TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hold_command_authorization_operation
    ON TABLE hold_command_authorization
    FIELDS operation_id, created_at;
