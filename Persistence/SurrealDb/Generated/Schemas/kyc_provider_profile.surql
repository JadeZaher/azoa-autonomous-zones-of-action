-- ============================================================
-- Table: kyc_provider_profile
-- Aggregate: Node-scoped KYC provider catalog
-- Guardrail: Operator-owned non-secret configuration; versioned CAS
-- Note: Secrets remain in the deployment secret store. Tenants select only enabled, runtime-ready profile keys.
-- ============================================================

DEFINE TABLE IF NOT EXISTS kyc_provider_profile SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_provider_profile TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS display_name ON TABLE kyc_provider_profile TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS adapter_key ON TABLE kyc_provider_profile TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS enabled ON TABLE kyc_provider_profile TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS policy_version ON TABLE kyc_provider_profile TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS assurance_level ON TABLE kyc_provider_profile TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS version ON TABLE kyc_provider_profile TYPE int
    DEFAULT 1;
DEFINE FIELD IF NOT EXISTS trust_revision ON TABLE kyc_provider_profile TYPE int
    DEFAULT 1;
DEFINE FIELD IF NOT EXISTS updated_by_avatar_id ON TABLE kyc_provider_profile TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE kyc_provider_profile TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE kyc_provider_profile TYPE datetime;
