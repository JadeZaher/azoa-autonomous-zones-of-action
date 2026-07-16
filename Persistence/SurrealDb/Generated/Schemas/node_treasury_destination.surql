-- ============================================================
-- Table: node_treasury_destination
-- Aggregate: NodeTreasuryDestination (Persistence/SurrealDb/Models/NodeTreasuryDestination.cs)
-- Guardrail: G6 SCHEMAFULL; one versioned treasury destination per chain/network
-- Note: Treasury destinations are routing policy, separate from fee pricing. Settlements freeze the destination address and version they use.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_treasury_destination_network VALUE ["Devnet", "Testnet", "Mainnet"];

DEFINE TABLE IF NOT EXISTS node_treasury_destination SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_treasury_destination TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS chain ON TABLE node_treasury_destination TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS network ON TABLE node_treasury_destination TYPE string
    ASSERT $value INSIDE $node_treasury_destination_network;
DEFINE FIELD IF NOT EXISTS address ON TABLE node_treasury_destination TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS version ON TABLE node_treasury_destination TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS updated_by_avatar_id ON TABLE node_treasury_destination TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_treasury_destination TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE node_treasury_destination TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_treasury_destination_chain_network
    ON TABLE node_treasury_destination
    FIELDS chain, network
    UNIQUE;
