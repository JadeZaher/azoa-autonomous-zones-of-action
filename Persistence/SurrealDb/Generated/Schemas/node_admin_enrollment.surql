-- ============================================================
-- Table: node_admin_enrollment
-- Aggregate: NodeAdminEnrollment (Persistence/SurrealDb/Models/NodeAdminEnrollment.cs)
-- Guardrail: G6 SCHEMAFULL; one-time node-admin enrollment
-- Note: Only SHA-256 of the 32-byte enrollment token is stored. start binds exactly one public key and a pending TOTP secret (started_at CAS); complete consumes the row (consumed_at CAS). Expired rows are swept hourly.
-- ============================================================

DEFINE TABLE IF NOT EXISTS node_admin_enrollment SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_enrollment TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS admin_id ON TABLE node_admin_enrollment TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS token_hash ON TABLE node_admin_enrollment TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE node_admin_enrollment TYPE datetime;
DEFINE FIELD IF NOT EXISTS started_at ON TABLE node_admin_enrollment TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS pending_public_key ON TABLE node_admin_enrollment TYPE option<string>;
DEFINE FIELD IF NOT EXISTS pending_fingerprint ON TABLE node_admin_enrollment TYPE option<string>;
DEFINE FIELD IF NOT EXISTS pending_label ON TABLE node_admin_enrollment TYPE option<string>;
DEFINE FIELD IF NOT EXISTS totp_secret_pending_protected ON TABLE node_admin_enrollment TYPE option<string>;
DEFINE FIELD IF NOT EXISTS failed_attempts ON TABLE node_admin_enrollment TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS consumed_at ON TABLE node_admin_enrollment TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_by ON TABLE node_admin_enrollment TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_admin_enrollment TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_enrollment_token_hash
    ON TABLE node_admin_enrollment
    FIELDS token_hash
    UNIQUE;

DEFINE INDEX IF NOT EXISTS node_admin_enrollment_admin
    ON TABLE node_admin_enrollment
    FIELDS admin_id;

DEFINE INDEX IF NOT EXISTS node_admin_enrollment_expires
    ON TABLE node_admin_enrollment
    FIELDS expires_at;
