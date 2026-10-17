-- ============================================================
-- Table: action_local_receipt
-- Aggregate: Atomic local action effects
-- Guardrail: Immutable command receipt
-- ============================================================

DEFINE TABLE IF NOT EXISTS action_local_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE action_local_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS run_id ON TABLE action_local_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS node_id ON TABLE action_local_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE action_local_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE action_local_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE action_local_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS output_json ON TABLE action_local_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS is_error ON TABLE action_local_receipt TYPE bool;
DEFINE FIELD IF NOT EXISTS message ON TABLE action_local_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE action_local_receipt TYPE datetime;
