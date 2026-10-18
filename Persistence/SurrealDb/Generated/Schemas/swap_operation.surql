-- ============================================================
-- Table: swap_operation
-- Aggregate: Swap
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS swap_operation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS request_key_hash ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS quote_id ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS quote_hash ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS state ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS current_stage_id ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS cost_accounting_version ON TABLE swap_operation TYPE int;
DEFINE FIELD IF NOT EXISTS confirmed_effect_network_fee ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS confirmed_cleanup_network_fee ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS confirmed_retained_rent ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS confirmed_released_rent ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS unresolved_rent_reservation ON TABLE swap_operation TYPE string;
DEFINE FIELD IF NOT EXISTS unresolved_rent_stage_count ON TABLE swap_operation TYPE int;
DEFINE FIELD IF NOT EXISTS cleanup_started ON TABLE swap_operation TYPE bool;
DEFINE FIELD IF NOT EXISTS settlement_receipt_json ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS cleanup_consent_json ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS cleanup_receipt_json ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recovery_history_cursor ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS action_run_id ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS action_node_id ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS action_intent_hash ON TABLE swap_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE swap_operation TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE swap_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE swap_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE swap_operation TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS swap_operation_owner
    ON TABLE swap_operation
    FIELDS actor_user_id, tenant_id, created_at;

DEFINE INDEX IF NOT EXISTS swap_operation_due
    ON TABLE swap_operation
    FIELDS state, next_attempt_at;
