-- ============================================================
-- Table: node_admin_audit_head
-- Aggregate: NodeAdminAuditHead (Persistence/SurrealDb/Models/NodeAdminAuditHead.cs)
-- Guardrail: G6 SCHEMAFULL; singleton tail of the node-admin audit chain
-- Note: Singleton (id = head). Advanced by a seq CAS in the same transaction as each audit append, which serializes writers; a loser re-reads the head and retries.
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_admin_audit_head SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_audit_head TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS seq ON TABLE node_admin_audit_head TYPE int;
DEFINE FIELD IF NOT EXISTS hash ON TABLE node_admin_audit_head TYPE string
    ASSERT $value != NONE AND $value != "";
