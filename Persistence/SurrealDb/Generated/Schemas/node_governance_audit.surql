-- ============================================================
-- Table: node_governance_audit
-- Aggregate: NodeGovernanceAudit (Persistence/SurrealDb/Models/NodeGovernanceAudit.cs)
-- Guardrail: G6 SCHEMAFULL; append-only node-governance audit
-- Note: Immutable audit trail for local node-governance policy changes. The store exposes no update/delete path.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_governance_audit_action VALUE ["ParametersUpdated"];

DEFINE TABLE IF NOT EXISTS node_governance_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_governance_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS action ON TABLE node_governance_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS actor_user_id ON TABLE node_governance_audit TYPE record<user>;
DEFINE FIELD IF NOT EXISTS previous_version ON TABLE node_governance_audit TYPE int;
DEFINE FIELD IF NOT EXISTS new_version ON TABLE node_governance_audit TYPE int;
DEFINE FIELD IF NOT EXISTS previous_allowed_chains ON TABLE node_governance_audit TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS previous_allowed_asset_types ON TABLE node_governance_audit TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS allowed_chains ON TABLE node_governance_audit TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS allowed_asset_types ON TABLE node_governance_audit TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS detail ON TABLE node_governance_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE node_governance_audit TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS actor ON TABLE node_governance_audit TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_governance_audit_occurred
    ON TABLE node_governance_audit
    FIELDS occurred_at;

DEFINE INDEX IF NOT EXISTS node_governance_audit_actor
    ON TABLE node_governance_audit
    FIELDS actor_user_id;
