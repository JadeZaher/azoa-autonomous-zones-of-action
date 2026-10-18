-- ============================================================
-- Table: webhook_registration
-- Aggregate: WebhookRegistration
-- Guardrail: Tenant unique, encrypted versioned secret
-- ============================================================

DEFINE TABLE IF NOT EXISTS webhook_registration SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE webhook_registration TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE webhook_registration TYPE record<user>;
DEFINE FIELD IF NOT EXISTS url ON TABLE webhook_registration TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS secret ON TABLE webhook_registration TYPE option<string>;
DEFINE FIELD IF NOT EXISTS encrypted_secret ON TABLE webhook_registration TYPE string
    DEFAULT ALWAYS "";
DEFINE FIELD IF NOT EXISTS version ON TABLE webhook_registration TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS destination_revision ON TABLE webhook_registration TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS secret_revision ON TABLE webhook_registration TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS secret_rotated_at ON TABLE webhook_registration TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE webhook_registration TYPE bool
    DEFAULT true;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE webhook_registration TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS webhook_registration_tenant_unique
    ON TABLE webhook_registration
    FIELDS tenant_id
    UNIQUE;
