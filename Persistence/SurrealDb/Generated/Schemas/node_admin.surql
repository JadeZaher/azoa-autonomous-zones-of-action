-- ============================================================
-- Table: node_admin
-- Aggregate: NodeAdminAccount (Persistence/SurrealDb/Models/NodeAdminAccount.cs)
-- Guardrail: G6 SCHEMAFULL; named node administrator, never a platform user
-- Note: A named node administrator. Separate from user: a node admin cannot act as a user and vice versa. admin_revision is the CAS token for every status/role/credential change and is carried in session tokens, so a bump ends every session of the admin.
-- Note: totp_secret_protected is a DataProtection ciphertext bound to the admin id; totp_last_step is the RFC 6238 replay guard advanced by CAS. See Services/NodeAdmin/AGENTS.md.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_admin_status VALUE ["PendingEnrollment", "PendingActivation", "Active", "Suspended", "Revoked"];

DEFINE TABLE IF NOT EXISTS node_admin SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS username ON TABLE node_admin TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS display_name ON TABLE node_admin TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS status ON TABLE node_admin TYPE string
    ASSERT $value INSIDE $node_admin_status;
DEFINE FIELD IF NOT EXISTS role_ids ON TABLE node_admin TYPE array<string>;
DEFINE FIELD IF NOT EXISTS admin_revision ON TABLE node_admin TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS session_revision ON TABLE node_admin TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS totp_secret_protected ON TABLE node_admin TYPE option<string>;
DEFINE FIELD IF NOT EXISTS totp_last_step ON TABLE node_admin TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS failed_attempts ON TABLE node_admin TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS locked_until ON TABLE node_admin TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_by ON TABLE node_admin TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_admin TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE node_admin TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_username
    ON TABLE node_admin
    FIELDS username
    UNIQUE;
