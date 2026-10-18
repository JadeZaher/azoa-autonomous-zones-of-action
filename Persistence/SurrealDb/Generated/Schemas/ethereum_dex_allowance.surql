-- ============================================================
-- Table: ethereum_dex_allowance
-- Aggregate: Swap
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS ethereum_dex_allowance SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS owner_address ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS token_address ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS executor_address ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS state ON TABLE ethereum_dex_allowance TYPE string;
DEFINE FIELD IF NOT EXISTS release_stage_id ON TABLE ethereum_dex_allowance TYPE option<string>;
DEFINE FIELD IF NOT EXISTS release_evidence_json ON TABLE ethereum_dex_allowance TYPE option<string>;
DEFINE FIELD IF NOT EXISTS version ON TABLE ethereum_dex_allowance TYPE int;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE ethereum_dex_allowance TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE ethereum_dex_allowance TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS ethereum_dex_allowance_resource
    ON TABLE ethereum_dex_allowance
    FIELDS protocol_identity, owner_address, token_address, executor_address
    UNIQUE;
