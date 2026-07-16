-- ============================================================
-- Table: node_governance_parameters
-- Aggregate: NodeGovernanceParameters (Persistence/SurrealDb/Models/NodeGovernanceParameters.cs)
-- Guardrail: G6 SCHEMAFULL; node-governed runtime allowlists
-- Note: Singleton local node policy. Null allowlist = unrestricted dimension; empty array = deny all values in that dimension. Runtime enforcement lives in NodeGovernanceGuard.
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_governance_parameters SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_governance_parameters TYPE string
    ASSERT $value != NONE AND $value != "";

-- Allowed chain names; null = unrestricted, empty = deny all
DEFINE FIELD IF NOT EXISTS allowed_chains ON TABLE node_governance_parameters TYPE option<array<string>>;

-- Allowed governed asset type names; null = unrestricted, empty = deny all
DEFINE FIELD IF NOT EXISTS allowed_asset_types ON TABLE node_governance_parameters TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS version ON TABLE node_governance_parameters TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS updated_by_avatar_id ON TABLE node_governance_parameters TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_governance_parameters TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE node_governance_parameters TYPE datetime;
