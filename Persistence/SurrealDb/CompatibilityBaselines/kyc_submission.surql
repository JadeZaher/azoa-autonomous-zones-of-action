-- ============================================================
-- Table: kyc_submission
-- Aggregate: KycSubmission (generic identity-verification module)
-- Guardrail: G6 SCHEMAFULL
-- Note: Provider-agnostic KYC ledger keyed to Avatar. The gate (IKycGateService) reads the most-recent APPROVED submission for an avatar rather than a denormalized level on the avatar table, so the shared avatar schema is untouched.
-- Note: status / provider persist as string enum names (JsonStringEnumConverter) to keep rows human-legible. The active-submission guard queries status IN (PENDING, IN_REVIEW).
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- KycSubmission.KycProvider
DEFINE PARAM IF NOT EXISTS $kyc_submission_provider VALUE ["MANUAL", "VERIFF"];
-- KycSubmission.KycStatus
DEFINE PARAM IF NOT EXISTS $kyc_submission_status VALUE ["PENDING", "IN_REVIEW", "APPROVED", "REJECTED", "EXPIRED"];

DEFINE TABLE IF NOT EXISTS kyc_submission SCHEMAFULL;

-- Core identity (record id is the Guid('N') of the submission)
DEFINE FIELD IF NOT EXISTS id ON TABLE kyc_submission TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owner avatar (Guid('N') hex record link); indexed for owner lookups
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE kyc_submission TYPE option<record<avatar>>;

-- Verification provider that owns this submission
DEFINE FIELD IF NOT EXISTS provider ON TABLE kyc_submission TYPE string
    ASSERT $value INSIDE $kyc_submission_provider;

-- Lifecycle status
DEFINE FIELD IF NOT EXISTS status ON TABLE kyc_submission TYPE string
    ASSERT $value INSIDE $kyc_submission_status;

-- Review metadata (set by the admin review path)
DEFINE FIELD IF NOT EXISTS reviewer_id ON TABLE kyc_submission TYPE option<string>;
DEFINE FIELD IF NOT EXISTS review_notes ON TABLE kyc_submission TYPE option<string>;
DEFINE FIELD IF NOT EXISTS rejection_reason ON TABLE kyc_submission TYPE option<string>;

-- Provider session linkage (manual provider stamps the avatar id as a pseudo-session)
DEFINE FIELD IF NOT EXISTS provider_session_id ON TABLE kyc_submission TYPE option<string>;
DEFINE FIELD IF NOT EXISTS provider_result ON TABLE kyc_submission TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS submitted_at ON TABLE kyc_submission TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS reviewed_at ON TABLE kyc_submission TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE kyc_submission TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE kyc_submission TYPE datetime;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE kyc_submission TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS kyc_submission_avatar_id
    ON TABLE kyc_submission
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS kyc_submission_status
    ON TABLE kyc_submission
    FIELDS status;
