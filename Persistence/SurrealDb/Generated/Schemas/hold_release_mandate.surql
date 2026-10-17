-- ============================================================
-- Table: hold_release_mandate
-- Aggregate: Exact automatic custody release consent
-- Guardrail: Immutable terms and first-egress authority
-- ============================================================

DEFINE TABLE IF NOT EXISTS hold_release_mandate SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hold_release_mandate TYPE option<string>;
DEFINE FIELD IF NOT EXISTS hold_record_id ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS terms_json ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS terms_hash ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS original_authority_json ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS current_authority_json ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS current_authorization_id ON TABLE hold_release_mandate TYPE option<string>;
DEFINE FIELD IF NOT EXISTS original_quote_json ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS workflow_source_json ON TABLE hold_release_mandate TYPE option<string>;
DEFINE FIELD IF NOT EXISTS exchange_source_json ON TABLE hold_release_mandate TYPE option<string>;
DEFINE FIELD IF NOT EXISTS status ON TABLE hold_release_mandate TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE hold_release_mandate TYPE option<string>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE hold_release_mandate TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE hold_release_mandate TYPE int;
DEFINE FIELD IF NOT EXISTS not_before ON TABLE hold_release_mandate TYPE datetime;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE hold_release_mandate TYPE datetime;
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE hold_release_mandate TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_release_mandate TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE hold_release_mandate TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hold_release_mandate_hold
    ON TABLE hold_release_mandate
    FIELDS hold_record_id, created_at;

DEFINE INDEX IF NOT EXISTS hold_release_mandate_due
    ON TABLE hold_release_mandate
    FIELDS status, next_attempt_at;
