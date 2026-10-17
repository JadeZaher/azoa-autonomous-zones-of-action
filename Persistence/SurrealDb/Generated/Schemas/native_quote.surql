-- ============================================================
-- Table: native_quote
-- Aggregate: Deployment
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS native_quote SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE native_quote TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE native_quote TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE native_quote TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE native_quote TYPE string;
DEFINE FIELD IF NOT EXISTS quote_json ON TABLE native_quote TYPE string;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE native_quote TYPE datetime;
