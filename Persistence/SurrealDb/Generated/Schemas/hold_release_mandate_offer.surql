-- ============================================================
-- Table: hold_release_mandate_offer
-- Aggregate: Immutable release mandate cost disclosure
-- Guardrail: Server quoted original owner terms
-- ============================================================

DEFINE TABLE IF NOT EXISTS hold_release_mandate_offer SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hold_release_mandate_offer TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE hold_release_mandate_offer TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hold_release_mandate_offer TYPE option<string>;
DEFINE FIELD IF NOT EXISTS hold_record_id ON TABLE hold_release_mandate_offer TYPE string;
DEFINE FIELD IF NOT EXISTS input_hash ON TABLE hold_release_mandate_offer TYPE string;
DEFINE FIELD IF NOT EXISTS request_json ON TABLE hold_release_mandate_offer TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE hold_release_mandate_offer TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_release_mandate_offer TYPE datetime;
