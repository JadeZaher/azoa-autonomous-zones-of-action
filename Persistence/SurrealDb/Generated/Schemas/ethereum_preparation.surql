-- ============================================================
-- Table: ethereum_preparation
-- Aggregate: Ethereum custody
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS ethereum_preparation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS network ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS sender ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS preparation_key ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS nonce ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS nonce_slot ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS call_fingerprint ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS owner_user_id ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS state ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS signed_transaction_base64 ON TABLE ethereum_preparation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS transaction_id ON TABLE ethereum_preparation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recovery_candidates_json ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS recovery_audit_json ON TABLE ethereum_preparation TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE ethereum_preparation TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE ethereum_preparation TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE ethereum_preparation TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS ethereum_preparation_live_nonce
    ON TABLE ethereum_preparation
    FIELDS protocol_identity, sender, nonce, nonce_slot
    UNIQUE;

DEFINE INDEX IF NOT EXISTS ethereum_preparation_owner
    ON TABLE ethereum_preparation
    FIELDS owner_user_id, created_at;
