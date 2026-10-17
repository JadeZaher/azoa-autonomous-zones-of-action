-- ============================================================
-- Table: nft_workflow_cost_acceptance
-- Aggregate: NFT lifecycle
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS nft_workflow_cost_acceptance SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS acting_tenant_id ON TABLE nft_workflow_cost_acceptance TYPE option<string>;
DEFINE FIELD IF NOT EXISTS execution_id ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS execution_version ON TABLE nft_workflow_cost_acceptance TYPE int;
DEFINE FIELD IF NOT EXISTS lifecycle_id ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS frozen_input_hash ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS receipt_hash ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS receipt_json ON TABLE nft_workflow_cost_acceptance TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE nft_workflow_cost_acceptance TYPE datetime;
