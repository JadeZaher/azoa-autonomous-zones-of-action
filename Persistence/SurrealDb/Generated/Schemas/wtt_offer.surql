-- ============================================================
-- Table: wtt_offer
-- Aggregate: Wtt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS wtt_offer SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS request_key_hash ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS source_quote_json ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS target_inspection_json ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS offer_hash ON TABLE wtt_offer TYPE string;
DEFINE FIELD IF NOT EXISTS return_of_operation_id ON TABLE wtt_offer TYPE option<string>;
DEFINE FIELD IF NOT EXISTS consumed_by_operation_id ON TABLE wtt_offer TYPE option<string>;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE wtt_offer TYPE datetime;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wtt_offer TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wtt_offer_owner
    ON TABLE wtt_offer
    FIELDS actor_user_id, tenant_id, created_at;
