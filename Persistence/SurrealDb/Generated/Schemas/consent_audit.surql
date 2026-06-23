-- ============================================================
-- Table: consent_audit
-- Aggregate: ConsentAuditEntry (Models/ConsentAuditEntry.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Append-only audit trail: one immutable row per grant, revoke, expiry, and tenant-driven sign decision (AC10/L1). Never updated or deleted. Independent of the best-effort webhook — this is the durable record of what AZOA decided.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $consent_audit_action VALUE ["Granted", "Revoked", "Expired", "TenantSignAllowed", "TenantSignDenied"];

DEFINE TABLE IF NOT EXISTS consent_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE consent_audit TYPE string;
DEFINE FIELD IF NOT EXISTS action ON TABLE consent_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS grant_id ON TABLE consent_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE consent_audit TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE consent_audit TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS scope ON TABLE consent_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE consent_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE consent_audit TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS consent_audit_by_tenant
    ON TABLE consent_audit
    FIELDS tenant_id, occurred_at;

DEFINE INDEX IF NOT EXISTS consent_audit_by_grant
    ON TABLE consent_audit
    FIELDS grant_id;
