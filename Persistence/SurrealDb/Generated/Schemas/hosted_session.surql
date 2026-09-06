-- ============================================================
-- Table: hosted_session
-- Aggregate: HostedSession (provider-agnostic hosted redirect flow)
-- Guardrail: G6 SCHEMAFULL
-- Note: Durable admission record for the begin -> hosted redirect -> untrusted callback -> authoritative re-fetch -> resume shape. Deliberately NOT KYC-specific: flow_kind discriminates KYC from a future fiat-in checkout. See Services/HostedSessions/AGENTS.md.
-- Note: state_version is a CAS token, not display metadata. A redelivered callback loses the conditional UPDATE instead of producing a second terminal write; the provider documents no dedup or retry semantics, so idempotency is entirely ours.
-- Note: provider_session_id and provider_correlation_id are secret-adjacent server-side correlation. Never project them into a tenant-facing response.
-- Note: B3 nullable-collision review: hosted_session_provider_session UNIQUE spans provider_session_id, which is option<string> and is NONE on every freshly claimed row. VERIFIED EMPIRICALLY against SurrealDB v3.1.4 (the pinned dev/CI image): two rows whose indexed tuple differs only by a NONE component both CREATE successfully, so many un-attached claims coexist and one subject holding an open claim cannot block another subject's ClaimAdmissionAsync. Explicit NULL is not reachable here either -- SCHEMAFULL option<string> refuses it outright ('Expected `none | string` but found `NULL`'). The index is therefore retained: it is a real dedup guarantee that two AZOA sessions never bind one vendor session, and the node-wide-brick scenario it was suspected of causing does not exist.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $hosted_session_state VALUE ["CLAIMED", "AWAITING_SUBJECT", "AWAITING_OUTCOME", "RESUMING", "COMPLETED", "FAILED", "ABANDONED"];

DEFINE TABLE IF NOT EXISTS hosted_session SCHEMAFULL;

-- Core identity (record id is the Guid('N') of the session)
DEFINE FIELD IF NOT EXISTS id ON TABLE hosted_session TYPE string
    ASSERT $value != NONE AND $value != "";

-- Flow discriminator + provider
DEFINE FIELD IF NOT EXISTS flow_kind ON TABLE hosted_session TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE hosted_session TYPE string
    ASSERT $value != NONE AND $value != "";

-- Durable admission claimed before any external side effect
DEFINE FIELD IF NOT EXISTS admission_key ON TABLE hosted_session TYPE string
    ASSERT $value != NONE AND $value != "";

-- Subject + tenancy
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE hosted_session TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hosted_session TYPE option<record<user>>;

-- Server-side provider correlation (never leaves the node)
DEFINE FIELD IF NOT EXISTS provider_session_id ON TABLE hosted_session TYPE option<string>;
DEFINE FIELD IF NOT EXISTS provider_correlation_id ON TABLE hosted_session TYPE option<string>;
DEFINE FIELD IF NOT EXISTS redirect_url ON TABLE hosted_session TYPE option<string>;

-- Lifecycle
DEFINE FIELD IF NOT EXISTS state ON TABLE hosted_session TYPE string
    ASSERT $value INSIDE $hosted_session_state;
DEFINE FIELD IF NOT EXISTS state_version ON TABLE hosted_session TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS authoritative_outcome ON TABLE hosted_session TYPE option<string>;
DEFINE FIELD IF NOT EXISTS outcome_detail ON TABLE hosted_session TYPE option<string>;
DEFINE FIELD IF NOT EXISTS callback_count ON TABLE hosted_session TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS resume_target_id ON TABLE hosted_session TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_date ON TABLE hosted_session TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE hosted_session TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE hosted_session TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS last_callback_at ON TABLE hosted_session TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS completed_at ON TABLE hosted_session TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hosted_session_admission
    ON TABLE hosted_session
    FIELDS flow_kind, provider_key, admission_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS hosted_session_provider_session
    ON TABLE hosted_session
    FIELDS flow_kind, provider_key, provider_session_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS hosted_session_subject
    ON TABLE hosted_session
    FIELDS subject_user_id;
