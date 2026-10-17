-- ============================================================
-- Table: wallet
-- Aggregate: Wallet (Models/Wallet.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: No balance field -- chain is source of truth (data-engine-decision).
-- Note: B3 review: wallet_user_chain_address UNIQUE is on (user_id, chain_type, address) -- all three NOT NULL; SurrealDB NULL-collision concern does not apply.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- Wallet.WalletTypeKind
DEFINE PARAM IF NOT EXISTS $wallet_wallet_type VALUE ["External", "Platform"];

DEFINE TABLE IF NOT EXISTS wallet SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE wallet TYPE string;
DEFINE FIELD IF NOT EXISTS user_id ON TABLE wallet TYPE record<user>;
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE wallet TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS address ON TABLE wallet TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS public_key ON TABLE wallet TYPE option<string>;
DEFINE FIELD IF NOT EXISTS label ON TABLE wallet TYPE option<string>;
DEFINE FIELD IF NOT EXISTS is_default ON TABLE wallet TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS wallet_type ON TABLE wallet TYPE string
    ASSERT $value INSIDE $wallet_wallet_type;
DEFINE FIELD IF NOT EXISTS encrypted_private_key ON TABLE wallet TYPE option<string>;
DEFINE FIELD IF NOT EXISTS encrypted_seed_phrase ON TABLE wallet TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE wallet TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS wallet_user_chain_address
    ON TABLE wallet
    FIELDS user_id, chain_type, address
    UNIQUE;

DEFINE INDEX IF NOT EXISTS wallet_user_chain
    ON TABLE wallet
    FIELDS user_id, chain_type;

DEFINE INDEX IF NOT EXISTS wallet_chain_address
    ON TABLE wallet
    FIELDS chain_type, address;
