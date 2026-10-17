-- ============================================================
-- Table: deployment_operation
-- Aggregate: Deployment
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS deployment_operation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS artifact_id ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS artifact_hash ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS request_json ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS action_run_id ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS action_node_id ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS action_intent_hash ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS state ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS stage ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS prepared_json ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS submission_authorized ON TABLE deployment_operation TYPE bool;
DEFINE FIELD IF NOT EXISTS history_json ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS resource_id ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS evidence_json ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recovery_mode ON TABLE deployment_operation TYPE string;
DEFINE FIELD IF NOT EXISTS recovery_child_id ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS parent_operation_id ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recovery_binding_hash ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recovery_evidence_json ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recovery_projected ON TABLE deployment_operation TYPE bool;
DEFINE FIELD IF NOT EXISTS replaces_operation_id ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS replaced_by_operation_id ON TABLE deployment_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE deployment_operation TYPE int;
DEFINE FIELD IF NOT EXISTS generation ON TABLE deployment_operation TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE deployment_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE deployment_operation TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS deployment_operation_owner
    ON TABLE deployment_operation
    FIELDS actor_user_id, created_at;

DEFINE INDEX IF NOT EXISTS deployment_operation_state
    ON TABLE deployment_operation
    FIELDS state, updated_at;
