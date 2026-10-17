-- ============================================================
-- Table: webhook_registration_audit
-- Aggregate: Webhook registration
-- Guardrail: Immutable exact-command receipt
-- ============================================================

DEFINE TABLE IF NOT EXISTS webhook_registration_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS registration_id ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS disposition ON TABLE webhook_registration_audit TYPE string
    DEFAULT 'Applied';
DEFINE FIELD IF NOT EXISTS receipt_json ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS reason ON TABLE webhook_registration_audit TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE webhook_registration_audit TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS webhook_registration_audit_tenant
    ON TABLE webhook_registration_audit
    FIELDS tenant_id, created_at;
