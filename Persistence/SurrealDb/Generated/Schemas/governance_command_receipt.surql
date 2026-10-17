-- ============================================================
-- Table: governance_command_receipt
-- Aggregate: Atomic governance command receipt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS governance_command_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS run_id ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS node_id ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS proposal_id ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS output_json ON TABLE governance_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS is_error ON TABLE governance_command_receipt TYPE bool;
DEFINE FIELD IF NOT EXISTS error ON TABLE governance_command_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE governance_command_receipt TYPE datetime;
