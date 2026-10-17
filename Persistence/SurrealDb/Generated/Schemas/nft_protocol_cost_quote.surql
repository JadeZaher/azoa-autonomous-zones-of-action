-- ============================================================
-- Table: nft_protocol_cost_quote
-- Aggregate: NFT lifecycle
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS nft_protocol_cost_quote SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE nft_protocol_cost_quote TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE nft_protocol_cost_quote TYPE string;
DEFINE FIELD IF NOT EXISTS acting_tenant_id ON TABLE nft_protocol_cost_quote TYPE string;
DEFINE FIELD IF NOT EXISTS lifecycle_id ON TABLE nft_protocol_cost_quote TYPE string;
DEFINE FIELD IF NOT EXISTS quote_hash ON TABLE nft_protocol_cost_quote TYPE string;
DEFINE FIELD IF NOT EXISTS quote_json ON TABLE nft_protocol_cost_quote TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE nft_protocol_cost_quote TYPE datetime;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE nft_protocol_cost_quote TYPE datetime;
