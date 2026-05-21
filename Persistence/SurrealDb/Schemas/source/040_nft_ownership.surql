-- ============================================================
-- Table: nft_ownership
-- Aggregate: AvatarNFT (Models/AvatarNFT.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: is_current distinguishes live ownership from historical transfer rows. Adapter enforces the (chain, contract, token_id, is_current=true) singleton invariant.
-- Note: B3 review: nft_chain_contract_token_current is intentionally NOT UNIQUE because is_current=false rows accumulate as transfer history; uniqueness of the (chain, contract, token_id, is_current=true) tuple is enforced by the adapter setting is_current=false on the prior row before inserting the new one (SurrealDB does not support partial UNIQUE INDEXes).
-- ============================================================

DEFINE TABLE nft_ownership SCHEMAFULL;

DEFINE FIELD id ON TABLE nft_ownership TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD avatar_id ON TABLE nft_ownership TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD chain_type ON TABLE nft_ownership TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD contract_address ON TABLE nft_ownership TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD token_id ON TABLE nft_ownership TYPE string
    ASSERT $value != NONE AND $value != "";

-- Token standard (ERC721, ERC1155, ARC3, ...)
DEFINE FIELD token_standard ON TABLE nft_ownership TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD metadata_uri ON TABLE nft_ownership TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD image_uri ON TABLE nft_ownership TYPE option<string>;
DEFINE FIELD name ON TABLE nft_ownership TYPE option<string>;
DEFINE FIELD description ON TABLE nft_ownership TYPE option<string>;

-- Attributes (flexible key->value bag)
DEFINE FIELD attributes ON TABLE nft_ownership TYPE option<object>;
DEFINE FIELD royalty_percentage ON TABLE nft_ownership TYPE decimal
    DEFAULT 0.0;
DEFINE FIELD royalty_recipient ON TABLE nft_ownership TYPE option<string>;
DEFINE FIELD is_soulbound ON TABLE nft_ownership TYPE bool
    DEFAULT false;
DEFINE FIELD is_transferable ON TABLE nft_ownership TYPE bool
    DEFAULT true;

-- is_current: true = live ownership; false = historical
DEFINE FIELD is_current ON TABLE nft_ownership TYPE bool
    DEFAULT true;
DEFINE FIELD current_owner ON TABLE nft_ownership TYPE option<string>;
DEFINE FIELD is_active ON TABLE nft_ownership TYPE bool
    DEFAULT true;
DEFINE FIELD minted_date ON TABLE nft_ownership TYPE datetime;
DEFINE FIELD last_transfer_date ON TABLE nft_ownership TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX nft_chain_contract_token_current
    ON TABLE nft_ownership
    FIELDS chain_type, contract_address, token_id, is_current;

DEFINE INDEX nft_avatar_id
    ON TABLE nft_ownership
    FIELDS avatar_id;

DEFINE INDEX nft_chain_contract
    ON TABLE nft_ownership
    FIELDS chain_type, contract_address;
