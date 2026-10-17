-- ============================================================
-- Table: nft_workflow_cost_retirement
-- Aggregate: NFT lifecycle
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS nft_workflow_cost_retirement SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE nft_workflow_cost_retirement TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE nft_workflow_cost_retirement TYPE string;
DEFINE FIELD IF NOT EXISTS acting_tenant_id ON TABLE nft_workflow_cost_retirement TYPE option<string>;
DEFINE FIELD IF NOT EXISTS execution_id ON TABLE nft_workflow_cost_retirement TYPE string;
DEFINE FIELD IF NOT EXISTS execution_version ON TABLE nft_workflow_cost_retirement TYPE int;
DEFINE FIELD IF NOT EXISTS request_json ON TABLE nft_workflow_cost_retirement TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE nft_workflow_cost_retirement TYPE string;
DEFINE FIELD IF NOT EXISTS reference_json ON TABLE nft_workflow_cost_retirement TYPE string;
DEFINE FIELD IF NOT EXISTS consent_json ON TABLE nft_workflow_cost_retirement TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE nft_workflow_cost_retirement TYPE datetime;
