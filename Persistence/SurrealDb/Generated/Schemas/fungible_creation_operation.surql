-- ============================================================
-- Table: fungible_creation_operation
-- Aggregate: Fungible creation
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS fungible_creation_operation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE fungible_creation_operation TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE fungible_creation_operation TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE fungible_creation_operation TYPE string;
DEFINE FIELD IF NOT EXISTS request_json ON TABLE fungible_creation_operation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE fungible_creation_operation TYPE string;
DEFINE FIELD IF NOT EXISTS state ON TABLE fungible_creation_operation TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE fungible_creation_operation TYPE int;
DEFINE FIELD IF NOT EXISTS generation ON TABLE fungible_creation_operation TYPE int;
DEFINE FIELD IF NOT EXISTS quote_expires_at ON TABLE fungible_creation_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS accepted_at ON TABLE fungible_creation_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS offered_quote_json ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS current_authority_id ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS current_authority_json ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS prepared_json ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS history_json ON TABLE fungible_creation_operation TYPE string;
DEFINE FIELD IF NOT EXISTS observation_json ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE fungible_creation_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fungible_creation_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE fungible_creation_operation TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fungible_creation_actor
    ON TABLE fungible_creation_operation
    FIELDS actor_user_id, created_at;
