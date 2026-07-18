-- ============================================================
-- Table: node_fee_atomic_group
-- Aggregate: NodeFeeAtomicGroup (Persistence/SurrealDb/Models/NodeFeeAtomicGroup.cs)
-- Guardrail: G6 SCHEMAFULL; immutable accepted two-leg chain group receipt
-- Note: One receipt is deterministically bound to one settlement. It records an accepted group only; independent chain observation remains required before terminalization.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- NodeFeeAtomicGroup.StateKind
DEFINE PARAM IF NOT EXISTS $node_fee_atomic_group_state VALUE ["Submitted", "PendingConfirmation", "Confirmed"];

DEFINE TABLE IF NOT EXISTS node_fee_atomic_group SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_fee_atomic_group TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS settlement_id ON TABLE node_fee_atomic_group TYPE record<node_fee_settlement>
    READONLY;
DEFINE FIELD IF NOT EXISTS group_identity ON TABLE node_fee_atomic_group TYPE string
    ASSERT string::matches($value, "^[0-9a-f]{64}$")
    READONLY;
DEFINE FIELD IF NOT EXISTS chain_group_id ON TABLE node_fee_atomic_group TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS source_address ON TABLE node_fee_atomic_group TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS primary_recipient_address ON TABLE node_fee_atomic_group TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS primary_transaction_id ON TABLE node_fee_atomic_group TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS treasury_transaction_id ON TABLE node_fee_atomic_group TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS state ON TABLE node_fee_atomic_group TYPE string
    ASSERT $value INSIDE $node_fee_atomic_group_state
    READONLY;
DEFINE FIELD IF NOT EXISTS accepted_at ON TABLE node_fee_atomic_group TYPE datetime
    DEFAULT time::now()
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_fee_atomic_group_settlement_unique
    ON TABLE node_fee_atomic_group
    FIELDS settlement_id
    UNIQUE;
