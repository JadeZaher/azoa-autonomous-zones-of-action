-- ============================================================
-- Table: consent_lifecycle_command
-- Aggregate: Immutable consent command receipt
-- ============================================================

DEFINE TABLE IF NOT EXISTS consent_lifecycle_command SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE consent_lifecycle_command TYPE string;
DEFINE FIELD IF NOT EXISTS grantor_id ON TABLE consent_lifecycle_command TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE consent_lifecycle_command TYPE string;
DEFINE FIELD IF NOT EXISTS grant_id ON TABLE consent_lifecycle_command TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE consent_lifecycle_command TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE consent_lifecycle_command TYPE string;
DEFINE FIELD IF NOT EXISTS receipt_json ON TABLE consent_lifecycle_command TYPE string;
DEFINE FIELD IF NOT EXISTS withdrawn ON TABLE consent_lifecycle_command TYPE bool;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE consent_lifecycle_command TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS consent_command_grantor
    ON TABLE consent_lifecycle_command
    FIELDS grantor_id, created_at;
