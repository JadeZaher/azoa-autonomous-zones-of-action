-- ============================================================
-- Table: ownership_receipt
-- Aggregate: CommercialPrimitivesService
-- Guardrail: G6 SCHEMAFULL; owner-scoped ownership receipt document
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $ownership_receipt_holder_class VALUE ["CONTRIBUTOR", "INVESTOR", "FOUNDER", "MEMBER"];
DEFINE PARAM IF NOT EXISTS $ownership_receipt_verification_status VALUE ["verified", "recorded_pending_chain", "sandbox_simulated"];

DEFINE TABLE IF NOT EXISTS ownership_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS owner_actor_id ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS receipt_number ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS allocation_or_sale_id ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS holder_subject ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS holder_class ON TABLE ownership_receipt TYPE string
    ASSERT $value INSIDE $ownership_receipt_holder_class
    DEFAULT "MEMBER";
DEFINE FIELD IF NOT EXISTS asset_symbol ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS asset_resource_id ON TABLE ownership_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS units_formatted ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS fiat_minor_units ON TABLE ownership_receipt TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS chain_tx_hash ON TABLE ownership_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS content_hash ON TABLE ownership_receipt TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS issued_at ON TABLE ownership_receipt TYPE datetime;
DEFINE FIELD IF NOT EXISTS verification_status ON TABLE ownership_receipt TYPE string
    ASSERT $value INSIDE $ownership_receipt_verification_status
    DEFAULT "recorded_pending_chain";

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS ownership_receipt_owner
    ON TABLE ownership_receipt
    FIELDS owner_actor_id, issued_at;

DEFINE INDEX IF NOT EXISTS ownership_receipt_number
    ON TABLE ownership_receipt
    FIELDS receipt_number
    UNIQUE;
