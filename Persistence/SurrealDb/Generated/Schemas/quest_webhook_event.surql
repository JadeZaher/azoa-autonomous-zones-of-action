-- ============================================================
-- Table: quest_webhook_event
-- Aggregate: QuestWebhookEvent (Models/QuestWebhookEvent.cs)
-- Guardrail: G6 SCHEMAFULL, transactional-outbox single-winner due-scan
-- Note: Generic quest.emit webhook transactional outbox (final-hardening F3). Generalizes the consent_webhook_event outbox to arbitrary tenant-defined events fired by an Emit quest node. One row == one quest.emit event owed to exactly one tenant. Written in the SAME logical write as the Emit node output (no dual-write); a polling delivery worker claims due rows and POSTs them with retry + idempotency id. Mirrors the consent_webhook_event outbox SHAPE exactly.
-- Note: Due-scan: status='Pending' AND next_attempt_at<=now, oldest first. Conditional transitions (MarkDelivered/Reschedule/DeadLetter) assert AffectedCount==1 — the same single-winner discipline as the saga step claim, so behaviour is safe under one worker (and identical across engines).
-- Note: BEST-EFFORT (notification): this row is a NOTIFICATION. Delivery never writes back to quest_run or quest_node_execution. The Emit node's serialized Output remains the tenant's authoritative settlement surface; the webhook is a convenience push.
-- Note: STRICT per-tenant isolation (H5): tenant_id scopes every delivery to ONLY that tenant's webhook_registration + secret (the SAME registration table the consent path uses). idempotency_id is the stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of this event.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestWebhookEvent.DeliveryStatus
DEFINE PARAM IF NOT EXISTS $quest_webhook_event_status VALUE ["Pending", "Delivered", "DeadLettered"];

DEFINE TABLE IF NOT EXISTS quest_webhook_event SCHEMAFULL;

-- Core identity
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_webhook_event TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning tenant — the per-tenant isolation key (H5). Resolves ONLY this tenant's registration + secret at delivery.
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE quest_webhook_event TYPE record<avatar>;

-- Event payload — the tenant-defined event name (free-form; AZOA does not interpret it)
DEFINE FIELD IF NOT EXISTS event_type ON TABLE quest_webhook_event TYPE string;

-- Event payload — the owning quest_run
DEFINE FIELD IF NOT EXISTS run_id ON TABLE quest_webhook_event TYPE record<quest_run>;

-- Event payload — the Emit definition node that fired the event
DEFINE FIELD IF NOT EXISTS node_id ON TABLE quest_webhook_event TYPE record<quest_node>;

-- Event payload — the quest definition the run instantiated
DEFINE FIELD IF NOT EXISTS quest_id ON TABLE quest_webhook_event TYPE record<quest>;

-- Event payload — the opaque tenant-shaped payload the Emit node carried (JSON string, echoed verbatim)
DEFINE FIELD IF NOT EXISTS payload_json ON TABLE quest_webhook_event TYPE string;

-- Event payload — when the emit occurred (business event time, distinct from the delivery timestamp)
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE quest_webhook_event TYPE datetime;

-- Delivery lifecycle — drives the due-scan + conditional transitions
DEFINE FIELD IF NOT EXISTS status ON TABLE quest_webhook_event TYPE string
    ASSERT $value INSIDE $quest_webhook_event_status
    DEFAULT "Pending";

-- Delivery attempts consumed so far
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE quest_webhook_event TYPE int
    DEFAULT 0;

-- Earliest UTC time the worker may (re)attempt delivery — pushed out by exponential backoff on failure; the due-scan selects rows where this has passed
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE quest_webhook_event TYPE datetime;

-- Last delivery error (HTTP status / exception text)
DEFINE FIELD IF NOT EXISTS last_error ON TABLE quest_webhook_event TYPE option<string>;

-- Stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of THIS event
DEFINE FIELD IF NOT EXISTS idempotency_id ON TABLE quest_webhook_event TYPE string
    ASSERT $value != NONE AND $value != "";

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE quest_webhook_event TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_webhook_event_due_scan
    ON TABLE quest_webhook_event
    FIELDS status, next_attempt_at;

DEFINE INDEX IF NOT EXISTS quest_webhook_event_by_tenant
    ON TABLE quest_webhook_event
    FIELDS tenant_id;
