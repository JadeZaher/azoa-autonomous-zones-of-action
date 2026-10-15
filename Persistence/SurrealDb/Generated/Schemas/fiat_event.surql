-- ============================================================
-- Table: fiat_event
-- Aggregate: FiatRail (Services/Fiat/StripeWebhookInbox.cs)
-- Guardrail: G6 SCHEMAFULL; one row per provider event id and it is the dedup anchor
-- Note: Idempotent inbox for signature-verified payment-rail webhooks. One row is ONE provider event. The record id is the SHA-256 of provider|provider_event_id, so the CREATE itself de-duplicates a redelivery before any handler runs; the unique index is the second line of the same defence.
-- Note: The row carries NO amount, currency, customer or card field, and never the raw body -- only payload_digest. Every figure a handler acts on is re-read from the provider server-side, so a webhook body physically cannot be the source of a credited amount. That is the retired sidecar's pricing invariant expressed as a schema, not as a review note.
-- Note: status is the delivery disposition, not the payment's: Received (stored, no handler finished), Applied (a handler completed the work), Ignored (no handler claims this event type), Failed (a handler failed and the provider should retry). An issuance that is submitted but not yet confirmed stays Received -- marking it Applied is what would make a stuck credit invisible.
-- Note: environment and livemode are recorded together on purpose. The webhook endpoint refuses a delivery whose livemode disagrees with the bound rail environment, so a test-mode event can never settle live value; keeping both on the row makes that refusal auditable after the fact.
-- Note: intent_id is the funding intent (hosted_session, flow_kind='fiat-checkout') this event concerns, recovered from client_reference_id or provider metadata. It is option<> because reversal events can arrive for a charge whose session correlation was lost, and an unmatched event must still be recorded rather than dropped.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $fiat_event_environment VALUE ["test", "live"];
DEFINE PARAM IF NOT EXISTS $fiat_event_status VALUE ["Received", "Applied", "Ignored", "Failed"];

DEFINE TABLE IF NOT EXISTS fiat_event SCHEMAFULL;

-- Core identity (record id is the SHA-256 of provider|provider event id)
DEFINE FIELD IF NOT EXISTS id ON TABLE fiat_event TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS provider ON TABLE fiat_event TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS provider_event_id ON TABLE fiat_event TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS event_type ON TABLE fiat_event TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS api_version ON TABLE fiat_event TYPE option<string>;

-- Environment (a test-mode event must never settle live value)
DEFINE FIELD IF NOT EXISTS environment ON TABLE fiat_event TYPE string
    ASSERT $value INSIDE $fiat_event_environment;
DEFINE FIELD IF NOT EXISTS livemode ON TABLE fiat_event TYPE bool
    DEFAULT false;

-- Delivery disposition
DEFINE FIELD IF NOT EXISTS status ON TABLE fiat_event TYPE string
    ASSERT $value INSIDE $fiat_event_status
    DEFAULT "Received";
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE fiat_event TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS last_error ON TABLE fiat_event TYPE option<string>;

-- Correlation back to the node's own records
DEFINE FIELD IF NOT EXISTS intent_id ON TABLE fiat_event TYPE option<record<hosted_session>>;
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE fiat_event TYPE option<record<user>>;

-- Provider-side correlation (server-side only; never tenant-projected)
DEFINE FIELD IF NOT EXISTS checkout_session_id ON TABLE fiat_event TYPE option<string>;
DEFINE FIELD IF NOT EXISTS payment_intent_id ON TABLE fiat_event TYPE option<string>;
DEFINE FIELD IF NOT EXISTS charge_id ON TABLE fiat_event TYPE option<string>;

-- Evidence of exactly which bytes were accepted
DEFINE FIELD IF NOT EXISTS payload_digest ON TABLE fiat_event TYPE string
    ASSERT $value != NONE AND $value != "";

-- Timestamps
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE fiat_event TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS received_at ON TABLE fiat_event TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS applied_at ON TABLE fiat_event TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fiat_event_provider_event
    ON TABLE fiat_event
    FIELDS provider, provider_event_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS fiat_event_intent
    ON TABLE fiat_event
    FIELDS intent_id, status;

DEFINE INDEX IF NOT EXISTS fiat_event_type
    ON TABLE fiat_event
    FIELDS event_type, status;

DEFINE INDEX IF NOT EXISTS fiat_event_charge
    ON TABLE fiat_event
    FIELDS charge_id;
