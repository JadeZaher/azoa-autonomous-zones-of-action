-- ============================================================
-- Table: exchange_hold_source
-- Aggregate: Exchange custody source
-- Guardrail: Original purchase authority and exact hold commitment
-- ============================================================

DEFINE TABLE IF NOT EXISTS exchange_hold_source SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_user_id ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS api_key_id ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS participant_user_id ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS request_key ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS state ON TABLE exchange_hold_source TYPE string;
DEFINE FIELD IF NOT EXISTS hold_command_id ON TABLE exchange_hold_source TYPE option<string>;
DEFINE FIELD IF NOT EXISTS hold_record_id ON TABLE exchange_hold_source TYPE option<string>;
DEFINE FIELD IF NOT EXISTS wire_intent_json ON TABLE exchange_hold_source TYPE option<string>;
DEFINE FIELD IF NOT EXISTS prepared_wire_json ON TABLE exchange_hold_source TYPE option<string>;
DEFINE FIELD IF NOT EXISTS prepared_hash ON TABLE exchange_hold_source TYPE option<string>;
DEFINE FIELD IF NOT EXISTS settlement_evidence_json ON TABLE exchange_hold_source TYPE option<string>;
DEFINE FIELD IF NOT EXISTS generation ON TABLE exchange_hold_source TYPE int;
DEFINE FIELD IF NOT EXISTS next_reconcile_at ON TABLE exchange_hold_source TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS version ON TABLE exchange_hold_source TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE exchange_hold_source TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE exchange_hold_source TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS exchange_hold_source_request
    ON TABLE exchange_hold_source
    FIELDS tenant_user_id, api_key_id, request_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS exchange_hold_source_pending
    ON TABLE exchange_hold_source
    FIELDS state, updated_at;

DEFINE INDEX IF NOT EXISTS exchange_hold_source_due
    ON TABLE exchange_hold_source
    FIELDS state, next_reconcile_at;

DEFINE INDEX IF NOT EXISTS exchange_hold_source_participant
    ON TABLE exchange_hold_source
    FIELDS participant_user_id, state;
