-- ============================================================
-- Table: action_webhook_event
-- Aggregate: Immutable accepted action webhook event
-- Guardrail: G6 SCHEMAFULL, immutable accepted event and versioned delivery
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ActionWebhookEvent.DeliveryStatus
DEFINE PARAM IF NOT EXISTS $action_webhook_event_status VALUE ["Pending", "Delivered", "DeadLettered"];

DEFINE TABLE IF NOT EXISTS action_webhook_event SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS receipt_id ON TABLE action_webhook_event TYPE option<string>;
DEFINE FIELD IF NOT EXISTS registration_id ON TABLE action_webhook_event TYPE option<string>;
DEFINE FIELD IF NOT EXISTS destination_revision ON TABLE action_webhook_event TYPE option<int>;
DEFINE FIELD IF NOT EXISTS destination_url ON TABLE action_webhook_event TYPE option<string>;
DEFINE FIELD IF NOT EXISTS body_json ON TABLE action_webhook_event TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE action_webhook_event TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS retry_generation ON TABLE action_webhook_event TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS cycle_attempt_count ON TABLE action_webhook_event TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS lease_id ON TABLE action_webhook_event TYPE option<string>;
DEFINE FIELD IF NOT EXISTS lease_expires_at ON TABLE action_webhook_event TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS attempt_secret_revision ON TABLE action_webhook_event TYPE option<int>;
DEFINE FIELD IF NOT EXISTS attempt_encrypted_secret ON TABLE action_webhook_event TYPE option<string>;

-- Core identity
DEFINE FIELD IF NOT EXISTS id ON TABLE action_webhook_event TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning tenant — the per-tenant isolation key (H5). Resolves ONLY this tenant's registration + secret at delivery.
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE action_webhook_event TYPE record<user>;

-- Event payload — the tenant-defined event name (free-form; AZOA does not interpret it)
DEFINE FIELD IF NOT EXISTS event_type ON TABLE action_webhook_event TYPE string;

-- Event payload — the owning action_run
DEFINE FIELD IF NOT EXISTS run_id ON TABLE action_webhook_event TYPE record<action_run>;

-- Event payload — the Emit definition node that fired the event
DEFINE FIELD IF NOT EXISTS node_id ON TABLE action_webhook_event TYPE record<action_node>;

-- Event payload — the action definition the run instantiated
DEFINE FIELD IF NOT EXISTS action_id ON TABLE action_webhook_event TYPE record<action>;

-- Event payload — the opaque tenant-shaped payload the Emit node carried (JSON string, echoed verbatim)
DEFINE FIELD IF NOT EXISTS payload_json ON TABLE action_webhook_event TYPE string;

-- Event payload — when the emit occurred (business event time, distinct from the delivery timestamp)
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE action_webhook_event TYPE datetime;

-- Delivery lifecycle — drives the due-scan + conditional transitions
DEFINE FIELD IF NOT EXISTS status ON TABLE action_webhook_event TYPE string
    ASSERT $value INSIDE $action_webhook_event_status
    DEFAULT "Pending";

-- Delivery attempts consumed so far
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE action_webhook_event TYPE int
    DEFAULT 0;

-- Earliest UTC time the worker may (re)attempt delivery — pushed out by exponential backoff on failure; the due-scan selects rows where this has passed
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE action_webhook_event TYPE datetime;

-- Last delivery error (HTTP status / exception text)
DEFINE FIELD IF NOT EXISTS last_error ON TABLE action_webhook_event TYPE option<string>;

-- Stable per-event dedup id sent to the receiver (X-Azoa-Idempotency-Id), constant across all retries of THIS event
DEFINE FIELD IF NOT EXISTS idempotency_id ON TABLE action_webhook_event TYPE string
    ASSERT $value != NONE AND $value != "";

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE action_webhook_event TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_webhook_event_due_scan
    ON TABLE action_webhook_event
    FIELDS status, next_attempt_at;

DEFINE INDEX IF NOT EXISTS action_webhook_event_by_tenant
    ON TABLE action_webhook_event
    FIELDS tenant_id;
