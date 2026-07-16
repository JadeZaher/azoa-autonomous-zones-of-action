-- ============================================================
-- Table: node_treasury_audit
-- Aggregate: NodeTreasuryAudit (Persistence/SurrealDb/Models/NodeTreasuryAudit.cs)
-- Guardrail: G6 SCHEMAFULL; append-only node-treasury audit
-- Note: Immutable audit trail for chain/network treasury routing changes. The store exposes no update or delete path.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_treasury_audit_action VALUE ["DestinationUpdated"];
DEFINE PARAM IF NOT EXISTS $node_treasury_audit_network VALUE ["Devnet", "Testnet", "Mainnet"];

DEFINE TABLE IF NOT EXISTS node_treasury_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_treasury_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS action ON TABLE node_treasury_audit TYPE string
    ASSERT $value INSIDE $node_treasury_audit_action;
DEFINE FIELD IF NOT EXISTS actor_avatar_id ON TABLE node_treasury_audit TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS chain ON TABLE node_treasury_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS network ON TABLE node_treasury_audit TYPE string
    ASSERT $value INSIDE $node_treasury_audit_network;
DEFINE FIELD IF NOT EXISTS previous_version ON TABLE node_treasury_audit TYPE int;
DEFINE FIELD IF NOT EXISTS new_version ON TABLE node_treasury_audit TYPE int;
DEFINE FIELD IF NOT EXISTS previous_destination_json ON TABLE node_treasury_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS destination_json ON TABLE node_treasury_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS detail ON TABLE node_treasury_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE node_treasury_audit TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_treasury_audit_occurred
    ON TABLE node_treasury_audit
    FIELDS occurred_at;

DEFINE INDEX IF NOT EXISTS node_treasury_audit_actor
    ON TABLE node_treasury_audit
    FIELDS actor_avatar_id;

DEFINE INDEX IF NOT EXISTS node_treasury_audit_chain_network
    ON TABLE node_treasury_audit
    FIELDS chain, network;
