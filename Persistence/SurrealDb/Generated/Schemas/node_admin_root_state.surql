-- ============================================================
-- Table: node_admin_root_state
-- Aggregate: NodeAdminRootState (Persistence/SurrealDb/Models/NodeAdminRootState.cs)
-- Guardrail: G6 SCHEMAFULL; break-glass root key revision watermark
-- Note: Singleton (id = local). root_key_revision only moves forward with NodeAdmin__RootKeyRevision; a lower env revision disables root login instead of rolling back. root_session_revision ends every root session when bumped. No key material is stored.
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_admin_root_state SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_root_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS root_key_revision ON TABLE node_admin_root_state TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS root_session_revision ON TABLE node_admin_root_state TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE node_admin_root_state TYPE datetime;
