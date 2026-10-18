-- ============================================================
-- Table: node_admin_credential
-- Aggregate: NodeAdminCredential (Persistence/SurrealDb/Models/NodeAdminCredential.cs)
-- Guardrail: G6 SCHEMAFULL; Ed25519 public key of a node admin
-- Note: Record id = fingerprint (sha256 of the raw 32-byte key, first 16 hex), so a key belongs to at most one admin forever; a revoked row may only be reactivated for the same admin. Only the public key is ever stored.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_admin_credential_kind VALUE ["ed25519"];

DEFINE TABLE IF NOT EXISTS node_admin_credential SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_credential TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS admin_id ON TABLE node_admin_credential TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS kind ON TABLE node_admin_credential TYPE string
    ASSERT $value INSIDE $node_admin_credential_kind;
DEFINE FIELD IF NOT EXISTS public_key ON TABLE node_admin_credential TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS fingerprint ON TABLE node_admin_credential TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS label ON TABLE node_admin_credential TYPE string;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_admin_credential TYPE datetime;
DEFINE FIELD IF NOT EXISTS revoked_at ON TABLE node_admin_credential TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_credential_fingerprint
    ON TABLE node_admin_credential
    FIELDS fingerprint
    UNIQUE;

DEFINE INDEX IF NOT EXISTS node_admin_credential_admin
    ON TABLE node_admin_credential
    FIELDS admin_id;
