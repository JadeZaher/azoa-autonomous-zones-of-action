-- ============================================================
-- Table: consent_participation_boundary
-- Aggregate: Consent membership admission and offboarding boundary
-- ============================================================

DEFINE TABLE IF NOT EXISTS consent_participation_boundary SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE consent_participation_boundary TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE consent_participation_boundary TYPE string;
DEFINE FIELD IF NOT EXISTS participation_ref ON TABLE consent_participation_boundary TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE consent_participation_boundary TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE consent_participation_boundary TYPE datetime;
DEFINE FIELD IF NOT EXISTS closed_at ON TABLE consent_participation_boundary TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS closure_receipt_json ON TABLE consent_participation_boundary TYPE option<string>;
DEFINE FIELD IF NOT EXISTS closure_authority_json ON TABLE consent_participation_boundary TYPE option<string>;
