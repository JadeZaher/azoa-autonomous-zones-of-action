-- ============================================================
-- Table: consent_webhook_event
-- Aggregate: ConsentWebhookEvent (Models/ConsentWebhookEvent.cs)
-- Guardrail: G6 SCHEMAFULL, transactional-outbox single-winner due-scan
-- Note: Outbound consent-webhook transactional outbox (tenant-consent-delegation §4, AC7). One row == one consent lifecycle event (granted/revoked/expired) owed to exactly one tenant. Written in the SAME logical transaction as the consent state change (no dual-write); a polling delivery worker claims due rows and POSTs them with retry + idempotency id. Mirrors the saga_steps outbox SHAPE exactly.
-- Note: Due-scan: status='Pending' AND next_attempt_at<=now, oldest first. Conditional transitions (MarkDelivered/Reschedule/DeadLetter) assert AffectedCount==1 — the same single-winner discipline as the saga step claim, so behaviour is safe under one worker (and identical across engines).
-- Note: OBSERVE-ONLY (AC8): this row is a NOTIFICATION. The signing seam is the enforcement path — a revoked grant is dead the instant revoked_at is written, independent of whether/when this event delivers. Delivery never writes back to consent_grant.
-- Note: STRICT per-tenant isolation (H5): tenant_id scopes every delivery to ONLY that tenant's webhook_registration + secret. idempotency_id is the stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of this event.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ConsentWebhookEvent.WebhookEventType
DEFINE PARAM IF NOT EXISTS $consent_webhook_event_event_type VALUE ["Granted", "Revoked", "Expired"];
-- ConsentWebhookEvent.DeliveryStatus
DEFINE PARAM IF NOT EXISTS $consent_webhook_event_status VALUE ["Pending", "Delivered", "DeadLettered"];

DEFINE TABLE IF NOT EXISTS consent_webhook_event SCHEMAFULL;

-- Core identity
DEFINE FIELD IF NOT EXISTS id ON TABLE consent_webhook_event TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning tenant — the per-tenant isolation key (H5). Resolves ONLY this tenant's registration + secret at delivery.
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE consent_webhook_event TYPE record<avatar>;

-- Which consent lifecycle transition this describes
DEFINE FIELD IF NOT EXISTS event_type ON TABLE consent_webhook_event TYPE string
    ASSERT $value INSIDE $consent_webhook_event_event_type;

-- Event payload — the consent_grant this event is about
DEFINE FIELD IF NOT EXISTS grant_id ON TABLE consent_webhook_event TYPE record<consent_grant>;

-- Event payload — the grantor user's avatar
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE consent_webhook_event TYPE record<avatar>;

-- Event payload — scopes (CSV, mirrors consent_grant.scopes)
DEFINE FIELD IF NOT EXISTS scopes ON TABLE consent_webhook_event TYPE string;

-- Event payload — opaque ArdaNova participation id (NONE for UserExplicit grants)
DEFINE FIELD IF NOT EXISTS participation_ref ON TABLE consent_webhook_event TYPE option<string>;

-- Event payload — when the consent transition occurred (business event time, distinct from the delivery timestamp)
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE consent_webhook_event TYPE datetime;

-- Delivery lifecycle — drives the due-scan + conditional transitions
DEFINE FIELD IF NOT EXISTS status ON TABLE consent_webhook_event TYPE string
    ASSERT $value INSIDE $consent_webhook_event_status
    DEFAULT "Pending";

-- Delivery attempts consumed so far
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE consent_webhook_event TYPE int
    DEFAULT 0;

-- Earliest UTC time the worker may (re)attempt delivery — pushed out by exponential backoff on failure; the due-scan selects rows where this has passed
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE consent_webhook_event TYPE datetime;

-- Last delivery error (HTTP status / exception text)
DEFINE FIELD IF NOT EXISTS last_error ON TABLE consent_webhook_event TYPE option<string>;

-- Stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of THIS event
DEFINE FIELD IF NOT EXISTS idempotency_id ON TABLE consent_webhook_event TYPE string
    ASSERT $value != NONE AND $value != "";

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE consent_webhook_event TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS consent_webhook_event_due_scan
    ON TABLE consent_webhook_event
    FIELDS status, next_attempt_at;

DEFINE INDEX IF NOT EXISTS consent_webhook_event_by_tenant
    ON TABLE consent_webhook_event
    FIELDS tenant_id;
