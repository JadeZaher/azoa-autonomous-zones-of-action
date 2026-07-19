-- ============================================================
-- Table: tenant_kyc_provider_selection
-- Aggregate: Tenant-selected KYC authority
-- Guardrail: One fixed-id row per tenant; versioned CAS
-- Note: A selection change invalidates prior attempts and approvals by revision; no provider secret is stored here.
-- ============================================================

DEFINE TABLE IF NOT EXISTS tenant_kyc_provider_selection SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE tenant_kyc_provider_selection TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE tenant_kyc_provider_selection TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE tenant_kyc_provider_selection TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS selection_version ON TABLE tenant_kyc_provider_selection TYPE int
    DEFAULT 1;
DEFINE FIELD IF NOT EXISTS updated_by_avatar_id ON TABLE tenant_kyc_provider_selection TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE tenant_kyc_provider_selection TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE tenant_kyc_provider_selection TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS tenant_kyc_provider_key
    ON TABLE tenant_kyc_provider_selection
    FIELDS provider_key;
