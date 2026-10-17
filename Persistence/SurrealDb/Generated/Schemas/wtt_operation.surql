-- ============================================================
-- Table: wtt_operation
-- Aggregate: Wtt
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $wtt_operation_state VALUE ["Admitted", "Active", "Ready", "AwaitingProof", "Settled", "Cancelled", "NeedsOperator"];
DEFINE PARAM IF NOT EXISTS $wtt_operation_phase VALUE ["Source", "Target", "NativeOutput", "Complete"];

DEFINE TABLE IF NOT EXISTS wtt_operation SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS request_key_hash ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS offer_id ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS offer_hash ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS source_quote_json ON TABLE wtt_operation TYPE string;
DEFINE FIELD IF NOT EXISTS target_quote_json ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS state ON TABLE wtt_operation TYPE string
    ASSERT $value INSIDE $wtt_operation_state;
DEFINE FIELD IF NOT EXISTS phase ON TABLE wtt_operation TYPE string
    ASSERT $value INSIDE $wtt_operation_phase;
DEFINE FIELD IF NOT EXISTS current_stage_id ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS resume_state ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS transfer_publication_json ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS attestation_publication_json ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS transfer_envelope_base64 ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS attestation_envelope_base64 ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS proofs_json ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS body_claim_id ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS settlement_json ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS return_of_operation_id ON TABLE wtt_operation TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE wtt_operation TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE wtt_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE wtt_operation TYPE datetime;
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE wtt_operation TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wtt_operation_owner
    ON TABLE wtt_operation
    FIELDS actor_user_id, tenant_id, created_at;

DEFINE INDEX IF NOT EXISTS wtt_operation_due
    ON TABLE wtt_operation
    FIELDS state, next_attempt_at;
