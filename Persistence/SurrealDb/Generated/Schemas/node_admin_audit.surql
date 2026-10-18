-- ============================================================
-- Table: node_admin_audit
-- Aggregate: NodeAdminAudit (Persistence/SurrealDb/Models/NodeAdminAudit.cs)
-- Guardrail: G6 SCHEMAFULL; append-only hash-chained node-admin audit
-- Note: hash = hex(sha256(prev_hash, seq, at, actor, action, target, outcome, detail joined by newlines)); genesis prev_hash is 64 zeros. Appended in one transaction with the node_admin_audit_head CAS; every field is READONLY and no update/delete path exists. Record id = seq zero-padded to 20 digits.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_admin_audit_outcome VALUE ["intent", "ok", "denied", "failed"];

DEFINE TABLE IF NOT EXISTS node_admin_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_admin_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS seq ON TABLE node_admin_audit TYPE int
    READONLY;
DEFINE FIELD IF NOT EXISTS at ON TABLE node_admin_audit TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS actor ON TABLE node_admin_audit TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS amr ON TABLE node_admin_audit TYPE array<string>
    READONLY;
DEFINE FIELD IF NOT EXISTS action ON TABLE node_admin_audit TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS target ON TABLE node_admin_audit TYPE string
    READONLY;
DEFINE FIELD IF NOT EXISTS outcome ON TABLE node_admin_audit TYPE string
    ASSERT $value INSIDE $node_admin_audit_outcome
    READONLY;
DEFINE FIELD IF NOT EXISTS detail ON TABLE node_admin_audit TYPE string
    READONLY;
DEFINE FIELD IF NOT EXISTS ip ON TABLE node_admin_audit TYPE string
    READONLY;
DEFINE FIELD IF NOT EXISTS request_id ON TABLE node_admin_audit TYPE string
    READONLY;
DEFINE FIELD IF NOT EXISTS prev_hash ON TABLE node_admin_audit TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS hash ON TABLE node_admin_audit TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_admin_audit_seq
    ON TABLE node_admin_audit
    FIELDS seq
    UNIQUE;
