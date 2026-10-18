-- ============================================================
-- Table: node_admin_challenge
-- Aggregate: NodeAdminChallenge (Persistence/SurrealDb/Models/NodeAdminChallenge.cs)
-- Guardrail: G6 SCHEMAFULL; single-use Ed25519 login challenge
-- Note: Single use: consumed by a conditional UPDATE (consumed_at = NONE AND expires_at > now) before the signature is checked. expires_at_text and audience are the exact strings the client signed; the server rebuilds the message from them.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_admin_challenge_purpose VALUE ["root_login", "admin_login", "enroll"];

DEFINE TABLE IF NOT EXISTS node_admin_challenge SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS purpose ON TABLE node_admin_challenge TYPE string
    ASSERT $value INSIDE $node_admin_challenge_purpose;
DEFINE FIELD IF NOT EXISTS nonce ON TABLE node_admin_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS subject ON TABLE node_admin_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS audience ON TABLE node_admin_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS expires_at ON TABLE node_admin_challenge TYPE datetime;
DEFINE FIELD IF NOT EXISTS expires_at_text ON TABLE node_admin_challenge TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS consumed_at ON TABLE node_admin_challenge TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_admin_challenge TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_challenge_nonce
    ON TABLE node_admin_challenge
    FIELDS nonce
    UNIQUE;

DEFINE INDEX IF NOT EXISTS node_admin_challenge_expires
    ON TABLE node_admin_challenge
    FIELDS expires_at;
