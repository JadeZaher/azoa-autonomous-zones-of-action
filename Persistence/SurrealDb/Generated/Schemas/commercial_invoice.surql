-- ============================================================
-- Table: commercial_invoice
-- Aggregate: CommercialPrimitivesService
-- Guardrail: G6 SCHEMAFULL; owner-scoped commercial invoice record
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $commercial_invoice_status VALUE ["draft", "open", "paid", "void", "expired"];

DEFINE TABLE IF NOT EXISTS commercial_invoice SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE commercial_invoice TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS owner_actor_id ON TABLE commercial_invoice TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS invoice_number ON TABLE commercial_invoice TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS title ON TABLE commercial_invoice TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS issuer_subject ON TABLE commercial_invoice TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS payer_subject ON TABLE commercial_invoice TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE commercial_invoice TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS subtotal_minor_units ON TABLE commercial_invoice TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS fee_minor_units ON TABLE commercial_invoice TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS total_minor_units ON TABLE commercial_invoice TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS token_equivalent_units ON TABLE commercial_invoice TYPE option<string>;
DEFINE FIELD IF NOT EXISTS line_items_json ON TABLE commercial_invoice TYPE string
    DEFAULT "[]";
DEFINE FIELD IF NOT EXISTS status ON TABLE commercial_invoice TYPE string
    ASSERT $value INSIDE $commercial_invoice_status
    DEFAULT "open";
DEFINE FIELD IF NOT EXISTS due_at ON TABLE commercial_invoice TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS payment_link_url ON TABLE commercial_invoice TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE commercial_invoice TYPE datetime;
DEFINE FIELD IF NOT EXISTS paid_at ON TABLE commercial_invoice TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS voided_at ON TABLE commercial_invoice TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS commercial_invoice_owner
    ON TABLE commercial_invoice
    FIELDS owner_actor_id, created_at;

DEFINE INDEX IF NOT EXISTS commercial_invoice_number
    ON TABLE commercial_invoice
    FIELDS invoice_number
    UNIQUE;
