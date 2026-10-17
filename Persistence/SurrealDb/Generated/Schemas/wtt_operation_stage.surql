-- ============================================================
-- Table: wtt_operation_stage
-- Aggregate: Wtt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $wtt_operation_stage_phase VALUE ["Source", "Target", "Participant", "NativeOutput", "Cleanup"];
DEFINE PARAM IF NOT EXISTS $wtt_operation_stage_state VALUE ["Planned", "Preparing", "Prepared", "Exposed", "SubmissionAuthorized", "Confirmed", "FailedOnChain", "ExpiredNoEffect", "NeedsOperator", "Cancelled", "Retired"];

DEFINE TABLE IF NOT EXISTS wtt_operation_stage SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wtt_operation_stage TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE wtt_operation_stage TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE wtt_operation_stage TYPE string;
DEFINE FIELD IF NOT EXISTS phase ON TABLE wtt_operation_stage TYPE string
    ASSERT $value INSIDE $wtt_operation_stage_phase;
DEFINE FIELD IF NOT EXISTS stage ON TABLE wtt_operation_stage TYPE string;
DEFINE FIELD IF NOT EXISTS generation ON TABLE wtt_operation_stage TYPE int;
DEFINE FIELD IF NOT EXISTS previous_stage_id ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS original_stage_id ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS quote_json ON TABLE wtt_operation_stage TYPE string;
DEFINE FIELD IF NOT EXISTS quote_hash ON TABLE wtt_operation_stage TYPE string;
DEFINE FIELD IF NOT EXISTS consent_json ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS participant_address ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS participant_json ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS prepared_json ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS prepared_hash ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS observation_json ON TABLE wtt_operation_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS state ON TABLE wtt_operation_stage TYPE string
    ASSERT $value INSIDE $wtt_operation_stage_state;
DEFINE FIELD IF NOT EXISTS exposed_at ON TABLE wtt_operation_stage TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS authorized_at ON TABLE wtt_operation_stage TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS version ON TABLE wtt_operation_stage TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wtt_operation_stage TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE wtt_operation_stage TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wtt_operation_stage_owner
    ON TABLE wtt_operation_stage
    FIELDS operation_id, created_at;
