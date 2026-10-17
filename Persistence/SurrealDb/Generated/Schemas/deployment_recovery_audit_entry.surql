-- ============================================================
-- Table: deployment_recovery_audit_entry
-- Aggregate: Deployment
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS deployment_recovery_audit_entry SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE deployment_recovery_audit_entry TYPE string;
DEFINE FIELD IF NOT EXISTS audit_id ON TABLE deployment_recovery_audit_entry TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE deployment_recovery_audit_entry TYPE string;
DEFINE FIELD IF NOT EXISTS successor_id ON TABLE deployment_recovery_audit_entry TYPE string;
DEFINE FIELD IF NOT EXISTS predecessor_id ON TABLE deployment_recovery_audit_entry TYPE option<string>;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE deployment_recovery_audit_entry TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE deployment_recovery_audit_entry TYPE string;
DEFINE FIELD IF NOT EXISTS history_hash ON TABLE deployment_recovery_audit_entry TYPE string;
DEFINE FIELD IF NOT EXISTS operation_version ON TABLE deployment_recovery_audit_entry TYPE int;
DEFINE FIELD IF NOT EXISTS position ON TABLE deployment_recovery_audit_entry TYPE int;
DEFINE FIELD IF NOT EXISTS digest ON TABLE deployment_recovery_audit_entry TYPE string;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS deployment_recovery_audit_visited
    ON TABLE deployment_recovery_audit_entry
    FIELDS audit_id, operation_id
    UNIQUE;
