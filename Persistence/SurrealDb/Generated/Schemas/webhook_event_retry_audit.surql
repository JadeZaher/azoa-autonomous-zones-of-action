-- ============================================================
-- Table: webhook_event_retry_audit
-- Aggregate: Webhook event retry
-- Guardrail: Immutable authorized exact-command receipt
-- ============================================================

DEFINE TABLE IF NOT EXISTS webhook_event_retry_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS event_id ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS receipt_json ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS reason ON TABLE webhook_event_retry_audit TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE webhook_event_retry_audit TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS webhook_event_retry_tenant
    ON TABLE webhook_event_retry_audit
    FIELDS tenant_id, created_at;
