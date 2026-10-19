-- ============================================================
-- Table: action_recovery_audit
-- Aggregate: Action recovery
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS action_recovery_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS operator_id ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS actor ON TABLE action_recovery_audit TYPE option<string>
    READONLY;
DEFINE FIELD IF NOT EXISTS execution_id ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS run_id ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS node_id ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS expected_version ON TABLE action_recovery_audit TYPE int;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS reason ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS action ON TABLE action_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE action_recovery_audit TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_recovery_audit_execution
    ON TABLE action_recovery_audit
    FIELDS execution_id, created_at;
