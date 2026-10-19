-- ============================================================
-- Table: node_admin_challenge
-- Aggregate: NodeAdminChallenge (Persistence/SurrealDb/Models/NodeAdminChallenge.cs)
-- Guardrail: G6 SCHEMAFULL; single-use Ed25519 login challenge
-- Note: Consumed-challenge markers only. Challenges are stateless (HMAC-authenticated ids, nothing written on issue); after a valid signature the server CREATEs a marker whose record id is the challenge id, so a second use collides and is refused as a replay. Rows are swept after expires_at.
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
