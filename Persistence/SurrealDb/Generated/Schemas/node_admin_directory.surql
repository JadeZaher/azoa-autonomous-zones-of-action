-- ============================================================
-- Table: node_admin_directory
-- Aggregate: NodeAdminDirectory (Persistence/SurrealDb/Models/NodeAdminDirectory.cs)
-- Guardrail: G6 SCHEMAFULL; authorization epoch for node-admin roles and statuses
-- Note: Singleton (id = local). Every admin status/role-assignment change and every role edit CASes revision in the same transaction (M1), so the last-owner check is atomic with the write. Also keys the in-process role cache.
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_admin_directory SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_directory TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS revision ON TABLE node_admin_directory TYPE int
    DEFAULT 0;
