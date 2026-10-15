-- ============================================================
-- Table: platform_token
-- Aggregate: PlatformTokenGenesis (Services/Reserve/PlatformTokenGenesis.cs)
-- Guardrail: G6 SCHEMAFULL; exactly one platform unit per (chain, network) -- genesis runs once and cannot be undone
-- Note: W3.2 (D49): the node-state record that says WHICH on-chain asset is this node's platform unit. appsettings.json cannot hold it because the asset id does not exist until the genesis mint returns, and the node cannot write its own configuration at runtime.
-- Note: The row is claimed BEFORE the mint (status Pending) and activated after it (status Active). Claim-then-mint is what makes a second genesis refusable: the unique index on (chain, network) means the claim is the single-winner gate, and a crash between claim and activation leaves a Pending row that a retry RESUMES under the same genesis_operation_key rather than a second asset.
-- Note: There is no Failed status on purpose. A mint that returns an error may still have broadcast, so a terminal Failed row could hide a real asset and invite a second one. A failed attempt stays Pending and records last_failure_reason; the retry replays through the fungible-token idempotency store.
-- Note: total_supply, decimals, unit_name and name are FROZEN at claim time. Changing any of them after genesis means a different Algorand ASA -- a second platform unit, not an edited one -- so a genesis request whose definition contradicts the claimed row is refused rather than applied.
-- Note: This row does NOT record circulating supply. Circulating supply is reserve OUTFLOW and is derived from platform_reserve_ledger partitions (issued - returned) at read time, so the two can never disagree.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $platform_token_network VALUE ["Devnet", "Testnet", "Mainnet"];
DEFINE PARAM IF NOT EXISTS $platform_token_status VALUE ["Pending", "Active"];

DEFINE TABLE IF NOT EXISTS platform_token SCHEMAFULL;

-- Core identity (record id is the SHA-256 of chain|network)
DEFINE FIELD IF NOT EXISTS id ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";

-- Where the platform unit lives
DEFINE FIELD IF NOT EXISTS chain ON TABLE platform_token TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS network ON TABLE platform_token TYPE string
    ASSERT $value INSIDE $platform_token_network;

-- Genesis lifecycle -- Pending is claimed-but-unminted, Active is minted
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

DEFINE INDEX IF NOT EXISTS platform_token_network_status
    ON TABLE platform_token
    FIELDS network, status;
