-- ============================================================
-- Table: admin_bootstrap_state
-- Aggregate: AdminBootstrapState (Persistence/SurrealDb/Models/AdminBootstrapState.cs)
-- Guardrail: G6 SCHEMAFULL; one-time bootstrap identity binding
-- Note: The local row binds a verified bootstrap to one immutable avatar record. It contains no bootstrap secret.
-- ============================================================

DEFINE TABLE IF NOT EXISTS admin_bootstrap_state SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE admin_bootstrap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE admin_bootstrap_state TYPE record<avatar>
    READONLY;
DEFINE FIELD IF NOT EXISTS activated_at ON TABLE admin_bootstrap_state TYPE datetime
    DEFAULT time::now()
    READONLY;
