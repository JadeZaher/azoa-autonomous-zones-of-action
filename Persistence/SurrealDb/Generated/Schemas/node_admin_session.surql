-- ============================================================
-- Table: node_admin_session
-- Aggregate: NodeAdminSession (Persistence/SurrealDb/Models/NodeAdminSession.cs)
-- Guardrail: G6 SCHEMAFULL; server-side node-admin session
-- Note: Record id = hex of the 128-bit sid (the token carries the base64url form). Every node-admin request requires an unrevoked, unexpired row whose principal equals the token subject (C2).
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_admin_session SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_session TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS principal ON TABLE node_admin_session TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS auth_time ON TABLE node_admin_session TYPE datetime;
DEFINE FIELD IF NOT EXISTS amr ON TABLE node_admin_session TYPE array<string>;
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE node_admin_session TYPE datetime;
DEFINE FIELD IF NOT EXISTS revoked_at ON TABLE node_admin_session TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS ip ON TABLE node_admin_session TYPE string;
DEFINE FIELD IF NOT EXISTS user_agent ON TABLE node_admin_session TYPE string;
DEFINE FIELD IF NOT EXISTS credential_fingerprint ON TABLE node_admin_session TYPE string;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_session_principal
    ON TABLE node_admin_session
    FIELDS principal;

DEFINE INDEX IF NOT EXISTS node_admin_session_expires
    ON TABLE node_admin_session
    FIELDS expires_at;
