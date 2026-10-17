-- ============================================================
-- Table: node_governance_policy_lock
-- Aggregate: Policy admission serialization
-- Guardrail: Singleton transaction lock
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_governance_policy_lock SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_governance_policy_lock TYPE string;
DEFINE FIELD IF NOT EXISTS token ON TABLE node_governance_policy_lock TYPE string;
