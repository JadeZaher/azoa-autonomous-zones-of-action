-- ============================================================
-- Table: tenant_price_schedule
-- Aggregate: TenantPriceSchedule (Services/Fiat/TenantPriceScheduleStore.cs)
-- Guardrail: G6 SCHEMAFULL; one row per (scope, chain, asset, version) and rows are append-only
-- Note: One row is ONE VERSION of the price at which money buys units of one asset. Rows are never updated in place: a new price is a new version, so every issuance can name the exact row that priced it and an auditor can re-derive the quantity from the money years later. The unique index on (scope_key, chain_type, asset_id, version) is what makes the version sequence single-winner.
-- Note: units_per_minor_unit is the ONLY direction stored: base units of asset_id per ONE minor unit of currency_code. At ARDA's 2 decimals a base of 1 ARDA = $0.01 is "100" -- one cent buys one whole ARDA, which is 100 base units. It is a string so a money constant never passes through a floating-point binder. The D16 stamp needs the reciprocal (minor units per base unit) and derives it; the exact per-issuance truth stays on on_ramp_lot, which records both the money and the units.
-- Note: scope_key is who the price belongs to: a tenant's compact user id, or the literal "node" for the operator's own default. It is a plain string rather than a nullable record so the version index can be unique across both cases -- a NONE tenant_id would not participate in a unique index the way a value does.
-- Note: status is Active or Retired. A retired latest version means the tenant has STOPPED selling units at any price: the on-ramp refuses rather than reaching further back for an older Active row, because an older row is a price nobody is offering any more.
-- Note: declared_at is when the TENANT observed the price, not when this node stored it. The D16 declared-rate source refuses a declaration dated after the settlement it would price and one older than the configured window, so the two instants have to stay distinguishable.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $tenant_price_schedule_status VALUE ["Active", "Retired"];

DEFINE TABLE IF NOT EXISTS tenant_price_schedule SCHEMAFULL;

-- Core identity (record id is the SHA-256 of scope|chain|asset|version)
DEFINE FIELD IF NOT EXISTS id ON TABLE tenant_price_schedule TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS scope_key ON TABLE tenant_price_schedule TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE tenant_price_schedule TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE tenant_price_schedule TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE tenant_price_schedule TYPE string
    ASSERT $value != NONE AND $value != "";

-- The price this version declares
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE tenant_price_schedule TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS version ON TABLE tenant_price_schedule TYPE int;
DEFINE FIELD IF NOT EXISTS units_per_minor_unit ON TABLE tenant_price_schedule TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS source ON TABLE tenant_price_schedule TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS note ON TABLE tenant_price_schedule TYPE option<string>;

-- Lifecycle
DEFINE FIELD IF NOT EXISTS status ON TABLE tenant_price_schedule TYPE string
    ASSERT $value INSIDE $tenant_price_schedule_status
    DEFAULT "Active";
DEFINE FIELD IF NOT EXISTS declared_at ON TABLE tenant_price_schedule TYPE datetime;
DEFINE FIELD IF NOT EXISTS registered_at ON TABLE tenant_price_schedule TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS registered_by_user_id ON TABLE tenant_price_schedule TYPE record<user>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS tenant_price_schedule_version
    ON TABLE tenant_price_schedule
    FIELDS scope_key, chain_type, asset_id, version
    UNIQUE;

DEFINE INDEX IF NOT EXISTS tenant_price_schedule_asset
    ON TABLE tenant_price_schedule
    FIELDS chain_type, asset_id, version;
