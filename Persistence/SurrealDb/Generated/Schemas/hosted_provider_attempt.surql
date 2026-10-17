-- ============================================================
-- Table: hosted_provider_attempt
-- ============================================================

DEFINE TABLE IF NOT EXISTS hosted_provider_attempt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS adapter_key ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS provider_key ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS origin ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS subject_id ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hosted_provider_attempt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS policy_revision ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS protocol_version ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS request_json ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS request_digest ON TABLE hosted_provider_attempt TYPE string;
DEFINE FIELD IF NOT EXISTS send_started_at ON TABLE hosted_provider_attempt TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS remote_session_id ON TABLE hosted_provider_attempt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS redirect_url ON TABLE hosted_provider_attempt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE hosted_provider_attempt TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS lease_token ON TABLE hosted_provider_attempt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS lease_expires_at ON TABLE hosted_provider_attempt TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE hosted_provider_attempt TYPE datetime;
DEFINE FIELD IF NOT EXISTS quarantined ON TABLE hosted_provider_attempt TYPE bool;
DEFINE FIELD IF NOT EXISTS recovery_completed ON TABLE hosted_provider_attempt TYPE bool;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hosted_provider_attempt TYPE datetime;
DEFINE FIELD IF NOT EXISTS version ON TABLE hosted_provider_attempt TYPE int;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hosted_provider_remote
    ON TABLE hosted_provider_attempt
    FIELDS adapter_key, origin, remote_session_id
    UNIQUE;
