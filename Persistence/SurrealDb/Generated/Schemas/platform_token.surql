-- ============================================================
-- Table: platform_token
-- Aggregate: PlatformTokenGenesis (Services/Reserve/PlatformTokenGenesis.cs)
-- Guardrail: G6 SCHEMAFULL; one immutable platform definition per network
-- Note: Finalized creation authority, reserve registration and activation commit atomically; see Services/Reserve/AGENTS.md.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $platform_token_network VALUE ["Devnet", "Testnet", "Mainnet"];
DEFINE PARAM IF NOT EXISTS $platform_token_status VALUE ["Pending", "Active"];

DEFINE TABLE IF NOT EXISTS platform_token SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS creation_operation_id ON TABLE platform_token TYPE option<string>;

-- Core identity (record id is the SHA-256 of chain|network)
DEFINE FIELD IF NOT EXISTS id ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";

-- Where the platform unit lives
DEFINE FIELD IF NOT EXISTS chain ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS network ON TABLE platform_token TYPE string
    ASSERT $value INSIDE $platform_token_network;

-- Genesis lifecycle -- Pending awaits final projection, Active records activation
DEFINE FIELD IF NOT EXISTS status ON TABLE platform_token TYPE string
    ASSERT $value INSIDE $platform_token_status;
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE platform_token TYPE option<string>;

-- The frozen genesis definition -- a change here means a SECOND asset, not an edit
DEFINE FIELD IF NOT EXISTS name ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS unit_name ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS decimals ON TABLE platform_token TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS total_supply ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";

-- Provenance of the one irreversible act
DEFINE FIELD IF NOT EXISTS reserve_address ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS genesis_operation_key ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS genesis_user_id ON TABLE platform_token TYPE record<user>;
DEFINE FIELD IF NOT EXISTS last_failure_reason ON TABLE platform_token TYPE option<string>;

-- Optimistic concurrency + timestamps
DEFINE FIELD IF NOT EXISTS version ON TABLE platform_token TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS claimed_at ON TABLE platform_token TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS activated_at ON TABLE platform_token TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE platform_token TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS platform_token_scope
    ON TABLE platform_token
    FIELDS chain, network
    UNIQUE;

DEFINE INDEX IF NOT EXISTS platform_token_network
    ON TABLE platform_token
    FIELDS network
    UNIQUE;

DEFINE INDEX IF NOT EXISTS platform_token_network_status
    ON TABLE platform_token
    FIELDS network, status;
