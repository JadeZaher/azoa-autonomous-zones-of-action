-- ============================================================
-- Table: tenant_payout_float
-- Aggregate: TenantPayoutFunding (Services/Fiat/OffRamp/TenantPayoutFunding.cs)
-- Guardrail: G6 SCHEMAFULL; one row per (tenant, currency, environment); counters move only under a version CAS together with the entry that justifies them
-- Note: available = credited - finalized - reserved. credited is the sum of AVAILABLE tenant_payout_credit rows net of their reversals; reserved and finalized are the sums over tenant_payout_debit rows in those states net of transfer reversals. See Services/Fiat/OffRamp/AGENTS.md §tenant-funded.
-- ============================================================

DEFINE TABLE IF NOT EXISTS tenant_payout_float SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE tenant_payout_float TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE tenant_payout_float TYPE record<user>;
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE tenant_payout_float TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS environment ON TABLE tenant_payout_float TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS credited_minor_units ON TABLE tenant_payout_float TYPE int;
DEFINE FIELD IF NOT EXISTS reserved_minor_units ON TABLE tenant_payout_float TYPE int;
DEFINE FIELD IF NOT EXISTS finalized_minor_units ON TABLE tenant_payout_float TYPE int;
DEFINE FIELD IF NOT EXISTS version ON TABLE tenant_payout_float TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE tenant_payout_float TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE tenant_payout_float TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS tenant_payout_float_scope
    ON TABLE tenant_payout_float
    FIELDS tenant_id, currency_code, environment
    UNIQUE;
