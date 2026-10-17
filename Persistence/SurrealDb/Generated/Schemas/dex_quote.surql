-- ============================================================
-- Table: dex_quote
-- Aggregate: Swap
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS dex_quote SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE dex_quote TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE dex_quote TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE dex_quote TYPE string;
DEFINE FIELD IF NOT EXISTS quote_hash ON TABLE dex_quote TYPE string;
DEFINE FIELD IF NOT EXISTS quote_json ON TABLE dex_quote TYPE string;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE dex_quote TYPE datetime;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE dex_quote TYPE datetime;
DEFINE FIELD IF NOT EXISTS consumed_by_operation_id ON TABLE dex_quote TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS dex_quote_actor
    ON TABLE dex_quote
    FIELDS actor_user_id, tenant_id, created_at;
