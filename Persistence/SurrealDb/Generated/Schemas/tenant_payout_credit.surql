-- ============================================================
-- Table: tenant_payout_credit
-- Aggregate: TenantPayoutFunding (Services/Fiat/OffRamp/TenantPayoutFunding.cs)
-- Guardrail: G6 SCHEMAFULL; record id derives from the checkout intent, so one credit per top-up
-- Note: net_minor_units is Stripe's balance_transaction.net (the tenant bears processing fees). reversed_minor_units is the authoritative refunded + dispute-withdrawn total re-read from Stripe (dispute fees included). A credit counts toward the float only in state Available: credited contribution = net - reversed, which may be negative. available_at absent means the rail declared no reversal window, and such a credit never matures.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $tenant_payout_credit_state VALUE ["Pending", "Available"];

DEFINE TABLE IF NOT EXISTS tenant_payout_credit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE tenant_payout_credit TYPE string;
DEFINE FIELD IF NOT EXISTS intent_id ON TABLE tenant_payout_credit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS float_id ON TABLE tenant_payout_credit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE tenant_payout_credit TYPE record<user>;
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE tenant_payout_credit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS environment ON TABLE tenant_payout_credit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS payment_intent_id ON TABLE tenant_payout_credit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS charge_id ON TABLE tenant_payout_credit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS gross_minor_units ON TABLE tenant_payout_credit TYPE int;
DEFINE FIELD IF NOT EXISTS net_minor_units ON TABLE tenant_payout_credit TYPE int;
DEFINE FIELD IF NOT EXISTS reversed_minor_units ON TABLE tenant_payout_credit TYPE int;
DEFINE FIELD IF NOT EXISTS state ON TABLE tenant_payout_credit TYPE string
    ASSERT $value INSIDE $tenant_payout_credit_state;
DEFINE FIELD IF NOT EXISTS available_at ON TABLE tenant_payout_credit TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS version ON TABLE tenant_payout_credit TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE tenant_payout_credit TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE tenant_payout_credit TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS tenant_payout_credit_intent
    ON TABLE tenant_payout_credit
    FIELDS intent_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS tenant_payout_credit_float
    ON TABLE tenant_payout_credit
    FIELDS float_id, state;

DEFINE INDEX IF NOT EXISTS tenant_payout_credit_payment
    ON TABLE tenant_payout_credit
    FIELDS payment_intent_id;
