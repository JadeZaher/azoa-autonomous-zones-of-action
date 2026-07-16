-- ============================================================
-- Table: node_fee_audit
-- Aggregate: NodeFeeAudit (Persistence/SurrealDb/Models/NodeFeeAudit.cs)
-- Guardrail: G6 SCHEMAFULL; append-only node-fee audit
-- Note: Immutable audit trail for node fee schedule changes. Stores previous/new schedule snapshots as JSON strings so operators can reconstruct every version.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_fee_audit_action VALUE ["ScheduleUpdated"];

DEFINE TABLE IF NOT EXISTS node_fee_audit SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_fee_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS action ON TABLE node_fee_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS actor_avatar_id ON TABLE node_fee_audit TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS previous_version ON TABLE node_fee_audit TYPE int;
DEFINE FIELD IF NOT EXISTS new_version ON TABLE node_fee_audit TYPE int;
DEFINE FIELD IF NOT EXISTS previous_schedule_json ON TABLE node_fee_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS schedule_json ON TABLE node_fee_audit TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS detail ON TABLE node_fee_audit TYPE option<string>;
DEFINE FIELD IF NOT EXISTS occurred_at ON TABLE node_fee_audit TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_fee_audit_occurred
    ON TABLE node_fee_audit
    FIELDS occurred_at;

DEFINE INDEX IF NOT EXISTS node_fee_audit_actor
    ON TABLE node_fee_audit
    FIELDS actor_avatar_id;
