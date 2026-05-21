-- ============================================================
-- Table: wallet
-- Aggregate: Wallet (Models/Wallet.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: No balance field -- chain is source of truth (data-engine-decision).
-- ============================================================

DEFINE TABLE wallet SCHEMAFULL;

DEFINE FIELD id ON TABLE wallet TYPE string;
DEFINE FIELD avatar_id ON TABLE wallet TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD chain_type ON TABLE wallet TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD address ON TABLE wallet TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD public_key ON TABLE wallet TYPE option<string>;
DEFINE FIELD label ON TABLE wallet TYPE option<string>;
DEFINE FIELD is_default ON TABLE wallet TYPE bool
    DEFAULT false;
DEFINE FIELD wallet_type ON TABLE wallet TYPE string
    ASSERT $value INSIDE ["External", "Platform"];
DEFINE FIELD encrypted_private_key ON TABLE wallet TYPE option<string>;
DEFINE FIELD encrypted_seed_phrase ON TABLE wallet TYPE option<string>;
DEFINE FIELD created_date ON TABLE wallet TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX wallet_avatar_chain_address
    ON TABLE wallet
    FIELDS avatar_id, chain_type, address
    UNIQUE;

DEFINE INDEX wallet_avatar_chain
    ON TABLE wallet
    FIELDS avatar_id, chain_type;
