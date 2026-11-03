-- ============================================================
-- Table: fiat_payout_request_fence
-- Aggregate: Cash-out admission or permanent void
-- Guardrail: G6 SCHEMAFULL; same id as fiat_payout
-- ============================================================

DEFINE TABLE IF NOT EXISTS fiat_payout_request_fence SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE fiat_payout_request_fence TYPE string;
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE fiat_payout_request_fence TYPE string;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fiat_payout_request_fence TYPE option<string>;
DEFINE FIELD IF NOT EXISTS request_key ON TABLE fiat_payout_request_fence TYPE string;
DEFINE FIELD IF NOT EXISTS environment ON TABLE fiat_payout_request_fence TYPE string;
DEFINE FIELD IF NOT EXISTS chain_network ON TABLE fiat_payout_request_fence TYPE string;
DEFINE FIELD IF NOT EXISTS verdict ON TABLE fiat_payout_request_fence TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fiat_payout_request_fence TYPE datetime;
