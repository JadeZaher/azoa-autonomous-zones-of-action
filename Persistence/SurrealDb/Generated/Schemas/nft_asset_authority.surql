-- ============================================================
-- Table: nft_asset_authority
-- Aggregate: NFT authority
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS nft_asset_authority SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS nft_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS network ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS contract_address ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS token_standard ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS issuer_user_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS creator_user_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS active_lifecycle_id ON TABLE nft_asset_authority TYPE option<string>;
DEFINE FIELD IF NOT EXISTS holder_user_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS owner_address ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS creation_transaction_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS issuance_lifecycle_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS latest_transaction_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS latest_lifecycle_id ON TABLE nft_asset_authority TYPE string;
DEFINE FIELD IF NOT EXISTS soulbound ON TABLE nft_asset_authority TYPE bool;
DEFINE FIELD IF NOT EXISTS burned ON TABLE nft_asset_authority TYPE bool;
DEFINE FIELD IF NOT EXISTS revoked ON TABLE nft_asset_authority TYPE bool;
DEFINE FIELD IF NOT EXISTS version ON TABLE nft_asset_authority TYPE int;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE nft_asset_authority TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS nft_authority_nft
    ON TABLE nft_asset_authority
    FIELDS nft_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS nft_authority_asset
    ON TABLE nft_asset_authority
    FIELDS chain_type, network, protocol_identity, contract_address, asset_id
    UNIQUE;
