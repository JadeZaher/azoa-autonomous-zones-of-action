-- ============================================================
-- Table: platform_reserve_ledger
-- Aggregate: PlatformReserve (Services/Reserve/PlatformReserveService.cs)
-- Guardrail: G6 SCHEMAFULL; one ledger row per (chain, network, asset) the platform reserve holds
-- Note: The node-side account of what the operator-owned reserve holds and has committed, per asset. It is NOT a balance ledger: chain truth is the balance. It records what the reserve has PROMISED out of each named partition, which chain state cannot express.
-- Note: partitions is a flexible object keyed by partition name (contributor, investor, or anything a tenant declares). Each value carries declared/issued/held/returned as exact decimal STRINGS so a supply larger than i64 is never truncated. Open by design: the partition vocabulary belongs to the tenant, so a closed enum would refuse legitimate future names.
-- Note: The oversubscription check runs here rather than only in a tenant database because sponsor exchanges and contributor holds draw from the SAME reserve. A partition policed in one caller's database can be overdrawn by the other caller.
-- Note: recent_draw_keys is a bounded FIFO replay window of applied draw operation keys. A retried draw whose key is still in the window is refused as a duplicate rather than double-counted; a key that has aged out of the window would be applied again, which is why callers anchor their own idempotency as well.
-- Note: version is the compare-and-swap anchor. Every partition move reads the row, applies the arithmetic in process, and writes back only while version is unchanged, so two concurrent draws cannot both spend the same availability.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $platform_reserve_ledger_network VALUE ["Devnet", "Testnet", "Mainnet"];

DEFINE TABLE IF NOT EXISTS platform_reserve_ledger SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE platform_reserve_ledger TYPE option<string>;

-- Core identity (record id is the SHA-256 of chain|network|asset id)
DEFINE FIELD IF NOT EXISTS id ON TABLE platform_reserve_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- The asset this row accounts for, and where it lives
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE platform_reserve_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS chain ON TABLE platform_reserve_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS network ON TABLE platform_reserve_ledger TYPE string
    ASSERT $value INSIDE $platform_reserve_ledger_network;
DEFINE FIELD IF NOT EXISTS reserve_address ON TABLE platform_reserve_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE platform_reserve_ledger TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS unit_name ON TABLE platform_reserve_ledger TYPE string;
DEFINE FIELD IF NOT EXISTS decimals ON TABLE platform_reserve_ledger TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS total_supply ON TABLE platform_reserve_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- Named partitions -- the node-side oversubscription check
DEFINE FIELD IF NOT EXISTS partitions ON TABLE platform_reserve_ledger TYPE option<object> FLEXIBLE;

-- Replay window for draw operation keys
DEFINE FIELD IF NOT EXISTS recent_draw_keys ON TABLE platform_reserve_ledger TYPE option<array<string>>;

-- Optimistic concurrency + timestamps
DEFINE FIELD IF NOT EXISTS version ON TABLE platform_reserve_ledger TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE platform_reserve_ledger TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE platform_reserve_ledger TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS platform_reserve_ledger_asset
    ON TABLE platform_reserve_ledger
    FIELDS chain, network, asset_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS platform_reserve_ledger_tenant
    ON TABLE platform_reserve_ledger
    FIELDS tenant_id;
