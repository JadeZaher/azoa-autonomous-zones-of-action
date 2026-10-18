-- ============================================================
-- Table: hold_command_receipt
-- Aggregate: Hold terminal command evidence
-- Guardrail: Immutable command outcome committed with custody accounting
-- ============================================================

DEFINE TABLE IF NOT EXISTS hold_command_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hold_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS hold_record_id ON TABLE hold_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE hold_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE hold_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hold_command_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS outcome_json ON TABLE hold_command_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS evidence_json ON TABLE hold_command_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_command_receipt TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hold_command_receipt_hold
    ON TABLE hold_command_receipt
    FIELDS hold_record_id, created_at;
