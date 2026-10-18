-- ============================================================
-- Table: swap_stage
-- Aggregate: Swap
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS swap_stage SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS stage ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS generation ON TABLE swap_stage TYPE int;
DEFINE FIELD IF NOT EXISTS preparation_key ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS previous_stage_id ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS plan_hash ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS plan_json ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS prepared_hash ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS prepared_json ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS state ON TABLE swap_stage TYPE string;
DEFINE FIELD IF NOT EXISTS observation_json ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS resolved_locked_balance ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS rent_accounting_evidence_json ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS rent_release_cleanup_stage_id ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS rent_release_scan_json ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS released_locked_balance ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS rent_release_evidence_json ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS authorized_at ON TABLE swap_stage TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS server_submission_withdrawn_at ON TABLE swap_stage TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS external_exposed_at ON TABLE swap_stage TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS external_exposure_hash ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS external_transaction_ids_json ON TABLE swap_stage TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE swap_stage TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE swap_stage TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE swap_stage TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS swap_stage_identity
    ON TABLE swap_stage
    FIELDS operation_id, stage, generation
    UNIQUE;
