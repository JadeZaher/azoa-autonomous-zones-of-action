-- ============================================================
-- Table: deployment_recovery_audit
-- Aggregate: Deployment
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS deployment_recovery_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS parent_operation_id ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS head_operation_id ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS head_intent_hash ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS head_predecessor_id ON TABLE deployment_recovery_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS next_operation_id ON TABLE deployment_recovery_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS expected_successor_id ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS digest ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS inspected_count ON TABLE deployment_recovery_audit TYPE int;
DEFINE FIELD IF NOT EXISTS state ON TABLE deployment_recovery_audit TYPE string;
DEFINE FIELD IF NOT EXISTS detail ON TABLE deployment_recovery_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE deployment_recovery_audit TYPE int;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE deployment_recovery_audit TYPE datetime;
