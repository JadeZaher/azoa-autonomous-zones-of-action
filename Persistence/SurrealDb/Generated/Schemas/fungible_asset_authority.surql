-- ============================================================
-- Table: fungible_asset_authority
-- Aggregate: Fungible creation
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS fungible_asset_authority SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS issuer_user_id ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fungible_asset_authority TYPE option<string>;
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS network ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS creator_address ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS creation_transaction_id ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS intent_hash ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS intent_json ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS evidence_json ON TABLE fungible_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fungible_asset_authority TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fungible_asset_identity
    ON TABLE fungible_asset_authority
    FIELDS chain_type, network, protocol_identity, asset_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS fungible_asset_operation
    ON TABLE fungible_asset_authority
    FIELDS operation_id
    UNIQUE;
