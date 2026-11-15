-- ============================================================
-- Table: commercial_payment_request
-- Aggregate: CommercialPrimitivesService
-- Guardrail: G6 SCHEMAFULL; owner-scoped commercial payment request record
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $commercial_payment_request_status VALUE ["draft", "open", "paid", "void", "expired"];

DEFINE TABLE IF NOT EXISTS commercial_payment_request SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE commercial_payment_request TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS owner_actor_id ON TABLE commercial_payment_request TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS request_number ON TABLE commercial_payment_request TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS title ON TABLE commercial_payment_request TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS requester_subject ON TABLE commercial_payment_request TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS payer_subject ON TABLE commercial_payment_request TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE commercial_payment_request TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS amount_minor_units ON TABLE commercial_payment_request TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS token_equivalent_units ON TABLE commercial_payment_request TYPE option<string>;
DEFINE FIELD IF NOT EXISTS memo ON TABLE commercial_payment_request TYPE option<string>;
DEFINE FIELD IF NOT EXISTS status ON TABLE commercial_payment_request TYPE string
    ASSERT $value INSIDE $commercial_payment_request_status
    DEFAULT "open";
DEFINE FIELD IF NOT EXISTS due_at ON TABLE commercial_payment_request TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS payment_link_url ON TABLE commercial_payment_request TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE commercial_payment_request TYPE datetime;
DEFINE FIELD IF NOT EXISTS paid_at ON TABLE commercial_payment_request TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS commercial_payment_request_owner
    ON TABLE commercial_payment_request
    FIELDS owner_actor_id, created_at;

DEFINE INDEX IF NOT EXISTS commercial_payment_request_number
    ON TABLE commercial_payment_request
    FIELDS request_number
    UNIQUE;
