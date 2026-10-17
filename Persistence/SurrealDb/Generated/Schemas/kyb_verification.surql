-- ============================================================
-- Table: kyb_verification
-- Aggregate: Tenant business verification
-- Guardrail: G6 SCHEMAFULL
-- Note: One current business-verification authority per tenant. Attempt and authority revision CAS prevents stale callbacks deciding a replacement attempt. Provider participant correlations never appear in tenant responses. See Services/Kyb/AGENTS.md.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $kyb_verification_status VALUE ["PENDING", "APPROVED", "REJECTED"];

DEFINE TABLE IF NOT EXISTS kyb_verification SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE kyb_verification TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS attempt_id ON TABLE kyb_verification TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS admission_key ON TABLE kyb_verification TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS request_key_hash ON TABLE kyb_verification TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS authority_revision ON TABLE kyb_verification TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS hosted_session_id ON TABLE kyb_verification TYPE option<string>;
DEFINE FIELD IF NOT EXISTS status ON TABLE kyb_verification TYPE string
    ASSERT $value INSIDE $kyb_verification_status;
DEFINE FIELD IF NOT EXISTS version ON TABLE kyb_verification TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS business_evidence_json ON TABLE kyb_verification TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE kyb_verification TYPE datetime;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE kyb_verification TYPE datetime;
DEFINE FIELD IF NOT EXISTS reviewed_at ON TABLE kyb_verification TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS effective_approval_expires_at ON TABLE kyb_verification TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS effective_approval_evidence_json ON TABLE kyb_verification TYPE option<string>;
DEFINE FIELD IF NOT EXISTS effective_approval_authority_revision ON TABLE kyb_verification TYPE option<string>;
