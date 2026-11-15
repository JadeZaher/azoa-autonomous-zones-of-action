-- ============================================================
-- Table: account_document
-- Aggregate: CommercialPrimitivesService
-- Guardrail: G6 SCHEMAFULL; owner-scoped account document report
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $account_document_document_type VALUE ["monthly_statement", "payout_ledger_report", "ownership_certificate", "kyc_consent_attestation", "tax_activity_summary"];

DEFINE TABLE IF NOT EXISTS account_document SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE account_document TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS owner_actor_id ON TABLE account_document TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS subject ON TABLE account_document TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS document_type ON TABLE account_document TYPE string
    ASSERT $value INSIDE $account_document_document_type
    DEFAULT "monthly_statement";
DEFINE FIELD IF NOT EXISTS title ON TABLE account_document TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS period_start ON TABLE account_document TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS period_end ON TABLE account_document TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS summary_metrics_json ON TABLE account_document TYPE string
    DEFAULT "{}";
DEFINE FIELD IF NOT EXISTS content_hash ON TABLE account_document TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS download_url ON TABLE account_document TYPE option<string>;
DEFINE FIELD IF NOT EXISTS generated_at ON TABLE account_document TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS account_document_owner
    ON TABLE account_document
    FIELDS owner_actor_id, generated_at;
