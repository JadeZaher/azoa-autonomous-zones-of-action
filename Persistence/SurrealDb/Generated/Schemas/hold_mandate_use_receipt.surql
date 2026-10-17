-- ============================================================
-- Table: hold_mandate_use_receipt
-- Aggregate: Immutable release mandate stage authority
-- Guardrail: Exact persisted authority and stage CAS
-- ============================================================

DEFINE TABLE IF NOT EXISTS hold_mandate_use_receipt SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE hold_mandate_use_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE hold_mandate_use_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS command_version ON TABLE hold_mandate_use_receipt TYPE int;
DEFINE FIELD IF NOT EXISTS stage ON TABLE hold_mandate_use_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS mandate_id ON TABLE hold_mandate_use_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS mandate_version ON TABLE hold_mandate_use_receipt TYPE int;
DEFINE FIELD IF NOT EXISTS terms_hash ON TABLE hold_mandate_use_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS authority_json ON TABLE hold_mandate_use_receipt TYPE string;
DEFINE FIELD IF NOT EXISTS workflow_source_json ON TABLE hold_mandate_use_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS exchange_source_json ON TABLE hold_mandate_use_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS authorization_id ON TABLE hold_mandate_use_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS prepared_hash ON TABLE hold_mandate_use_receipt TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_mandate_use_receipt TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hold_mandate_use_operation
    ON TABLE hold_mandate_use_receipt
    FIELDS operation_id, command_version
    UNIQUE;
