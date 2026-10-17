-- ============================================================
-- Table: nft_lifecycle
-- Aggregate: NFT lifecycle
-- Guardrail: G6 SCHEMAFULL
-- ============================================================

DEFINE TABLE IF NOT EXISTS nft_lifecycle SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS recipient_user_id ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS nft_id ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS request_hash ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS request_json ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS operation ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS network ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS stage ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS state ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS version ON TABLE nft_lifecycle TYPE int;
DEFINE FIELD IF NOT EXISTS preparation_generation ON TABLE nft_lifecycle TYPE int;
DEFINE FIELD IF NOT EXISTS confirmed_stage_count ON TABLE nft_lifecycle TYPE int;
DEFINE FIELD IF NOT EXISTS prepared_transaction_json ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS transaction_history_json ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS stage_consents_json ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS replacement_cost_consents_json ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS replacement_cost_retirements_json ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS actor_continuations_json ON TABLE nft_lifecycle TYPE string;
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS contract_address ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS token_standard ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS creation_transaction_id ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS final_transaction_id ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE nft_lifecycle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE nft_lifecycle TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE nft_lifecycle TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS nft_lifecycle_actor_created
    ON TABLE nft_lifecycle
    FIELDS actor_user_id, created_at;

DEFINE INDEX IF NOT EXISTS nft_lifecycle_recipient_created
    ON TABLE nft_lifecycle
    FIELDS recipient_user_id, created_at;
