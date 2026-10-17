-- ============================================================
-- Table: consent_participation_approval
-- Aggregate: Tenant-approved subject enrollment evidence
-- ============================================================

DEFINE TABLE IF NOT EXISTS consent_participation_approval SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE consent_participation_approval TYPE string;
DEFINE FIELD IF NOT EXISTS membership_id ON TABLE consent_participation_approval TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE consent_participation_approval TYPE string;
DEFINE FIELD IF NOT EXISTS grantor_id ON TABLE consent_participation_approval TYPE string;
DEFINE FIELD IF NOT EXISTS source_command_id ON TABLE consent_participation_approval TYPE string;
DEFINE FIELD IF NOT EXISTS source_kind ON TABLE consent_participation_approval TYPE string;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE consent_participation_approval TYPE bool;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE consent_participation_approval TYPE datetime;
