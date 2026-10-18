-- ============================================================
-- Table: node_admin_session
-- Aggregate: NodeAdminSession (Persistence/SurrealDb/Models/NodeAdminSession.cs)
-- Guardrail: G6 SCHEMAFULL; server-side node-admin session
-- Note: Record id = sha256 hex of the raw 128-bit sid; only the token carries the sid itself, so a database read never yields a usable session id. handle is an independent random id used to list and revoke sessions. Every node-admin request requires an unrevoked, unexpired row whose principal equals the token subject (C2).
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
DEFINE FIELD IF NOT EXISTS handle ON TABLE node_admin_session TYPE string
    ASSERT $value != NONE AND $value != "";

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_session_principal
    ON TABLE node_admin_session
    FIELDS principal;

DEFINE INDEX IF NOT EXISTS node_admin_session_handle
    ON TABLE node_admin_session
    FIELDS handle
    UNIQUE;

DEFINE INDEX IF NOT EXISTS node_admin_session_expires
    ON TABLE node_admin_session
    FIELDS expires_at;
