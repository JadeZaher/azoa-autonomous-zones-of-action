-- ============================================================
-- Table: hold_command
-- Aggregate: Durable hold command
-- Guardrail: Immutable intent and unique custody command
-- ============================================================

DEFINE TABLE IF NOT EXISTS hold_command SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recipient_user_id ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS hold_record_id ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS kind ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS state ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS stage ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS generation ON TABLE hold_command TYPE int;
DEFINE FIELD IF NOT EXISTS stage_intent_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS rail_plan_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS workflow_mandate_draft_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS accepted_quote_id ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS accepted_at ON TABLE hold_command TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS current_authority_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS current_authorization_id ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS transaction_history_json ON TABLE hold_command TYPE string;
DEFINE FIELD IF NOT EXISTS prepared_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS acceptance_intent_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS acceptance_request_id ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS acceptance_request_hash ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS acceptance_authority_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS current_acceptance_authority_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS current_acceptance_authorization_id ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS acceptance_state ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS acceptance_prepared_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS acceptance_evidence_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS observation_json ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE hold_command TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE hold_command TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_command TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE hold_command TYPE datetime;
DEFINE FIELD IF NOT EXISTS next_reconcile_at ON TABLE hold_command TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hold_command_actor
    ON TABLE hold_command
    FIELDS actor_user_id, tenant_id, created_at;

DEFINE INDEX IF NOT EXISTS hold_command_hold
    ON TABLE hold_command
    FIELDS hold_record_id, created_at;

DEFINE INDEX IF NOT EXISTS hold_command_recipient
    ON TABLE hold_command
    FIELDS recipient_user_id, created_at;

DEFINE INDEX IF NOT EXISTS hold_command_recovery
    ON TABLE hold_command
    FIELDS state, updated_at;

DEFINE INDEX IF NOT EXISTS hold_command_due
    ON TABLE hold_command
    FIELDS next_reconcile_at, id;
