-- ============================================================
-- Table: kyc_control_audit
-- Aggregate: Application-append-only KYC control-plane audit
-- Guardrail: The store exposes create-only operator and tenant configuration evidence
-- ============================================================

DEFINE TABLE IF NOT EXISTS kyc_control_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_control_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS action ON TABLE kyc_control_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE kyc_control_audit TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE kyc_control_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS previous_provider_key ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE kyc_control_audit TYPE int;
DEFINE FIELD IF NOT EXISTS previous_display_name ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS display_name ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS previous_adapter_key ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS adapter_key ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS previous_enabled ON TABLE kyc_control_audit TYPE option<bool>;
DEFINE FIELD IF NOT EXISTS enabled ON TABLE kyc_control_audit TYPE option<bool>;
DEFINE FIELD IF NOT EXISTS previous_policy_version ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS policy_version ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS previous_assurance_level ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS assurance_level ON TABLE kyc_control_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS previous_trust_revision ON TABLE kyc_control_audit TYPE option<int>;
DEFINE FIELD IF NOT EXISTS trust_revision ON TABLE kyc_control_audit TYPE option<int>;
DEFINE FIELD IF NOT EXISTS actor_avatar_id ON TABLE kyc_control_audit TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE kyc_control_audit TYPE datetime
    DEFAULT time::now()
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS kyc_control_audit_tenant
    ON TABLE kyc_control_audit
    FIELDS tenant_id, occurred_at;
