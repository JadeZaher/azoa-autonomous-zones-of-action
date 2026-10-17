-- ============================================================
-- Table: ethereum_signer_checkpoint
-- Aggregate: Ethereum custody
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS ethereum_signer_checkpoint SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE ethereum_signer_checkpoint TYPE string;
DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE ethereum_signer_checkpoint TYPE string;
DEFINE FIELD IF NOT EXISTS sender ON TABLE ethereum_signer_checkpoint TYPE string;
DEFINE FIELD IF NOT EXISTS activation_epoch ON TABLE ethereum_signer_checkpoint TYPE string;
DEFINE FIELD IF NOT EXISTS initial_nonce ON TABLE ethereum_signer_checkpoint TYPE string;
DEFINE FIELD IF NOT EXISTS block_hash ON TABLE ethereum_signer_checkpoint TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE ethereum_signer_checkpoint TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE ethereum_signer_checkpoint TYPE datetime;
