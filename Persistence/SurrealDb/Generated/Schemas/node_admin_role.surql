-- ============================================================
-- Table: node_admin_role
-- Aggregate: NodeAdminRole (Persistence/SurrealDb/Models/NodeAdminRole.cs)
-- Guardrail: G6 SCHEMAFULL; node-admin RBAC role
-- Note: Built-in roles are seeded idempotently at boot and immutable. permissions is validated against the closed Core/NodeAdminPermissions catalog on every write.
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_admin_role SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_role TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS name ON TABLE node_admin_role TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS built_in ON TABLE node_admin_role TYPE bool;
DEFINE FIELD IF NOT EXISTS permissions ON TABLE node_admin_role TYPE array<string>;
DEFINE FIELD IF NOT EXISTS revision ON TABLE node_admin_role TYPE int
    DEFAULT 1;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_admin_role TYPE datetime;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE node_admin_role TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_role_name
    ON TABLE node_admin_role
    FIELDS name
    UNIQUE;
