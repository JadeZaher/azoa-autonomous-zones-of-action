-- ============================================================
-- Table: tenant_payout_debit
-- Aggregate: TenantPayoutFunding (Services/Fiat/OffRamp/TenantPayoutFunding.cs)
-- Guardrail: G6 SCHEMAFULL; record id = payout id and payout_id is UNIQUE, so one debit per cash-out
-- Note: state: Reserved (coverage taken, money may or may not have left), Finalized (the bank payout is paid), Released (no transfer ever existed). reversed_minor_units = max(stored, Stripe amount_reversed) in Reserved and Finalized, so a transfer reversal or a failed-bank restitution gives the tenant back exactly what Stripe returned, once.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $tenant_payout_debit_state VALUE ["Reserved", "Finalized", "Released"];

DEFINE TABLE IF NOT EXISTS tenant_payout_debit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE tenant_payout_debit TYPE string;
DEFINE FIELD IF NOT EXISTS payout_id ON TABLE tenant_payout_debit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS float_id ON TABLE tenant_payout_debit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE tenant_payout_debit TYPE record<user>;
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE tenant_payout_debit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS environment ON TABLE tenant_payout_debit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS amount_minor_units ON TABLE tenant_payout_debit TYPE int;
DEFINE FIELD IF NOT EXISTS reversed_minor_units ON TABLE tenant_payout_debit TYPE int;
DEFINE FIELD IF NOT EXISTS state ON TABLE tenant_payout_debit TYPE string
    ASSERT $value INSIDE $tenant_payout_debit_state;
DEFINE FIELD IF NOT EXISTS version ON TABLE tenant_payout_debit TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE tenant_payout_debit TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE tenant_payout_debit TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS tenant_payout_debit_payout
    ON TABLE tenant_payout_debit
    FIELDS payout_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS tenant_payout_debit_float
    ON TABLE tenant_payout_debit
    FIELDS float_id, state;

DEFINE INDEX IF NOT EXISTS tenant_payout_debit_state
    ON TABLE tenant_payout_debit
    FIELDS state, created_at;
