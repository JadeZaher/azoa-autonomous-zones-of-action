-- ============================================================
-- Table: action_emit_receipt
-- Aggregate: Durable Emit acceptance
-- Guardrail: Immutable receipt and outbox commit together
-- ============================================================

DEFINE TABLE IF NOT EXISTS action_emit_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS run_id ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS node_id ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS actor_id ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE action_emit_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS action_id ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS output_json ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS event_type ON TABLE action_emit_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS event_id ON TABLE action_emit_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS event_body_hash ON TABLE action_emit_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS registration_id ON TABLE action_emit_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS destination_revision ON TABLE action_emit_receipt TYPE option<int>;
DEFINE FIELD IF NOT EXISTS destination_url ON TABLE action_emit_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS is_error ON TABLE action_emit_receipt TYPE bool;
DEFINE FIELD IF NOT EXISTS message ON TABLE action_emit_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE action_emit_receipt TYPE datetime;
