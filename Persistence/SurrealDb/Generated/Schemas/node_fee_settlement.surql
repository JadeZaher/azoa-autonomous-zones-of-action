-- ============================================================
-- Table: node_fee_settlement
-- Aggregate: NodeFeeSettlement (Persistence/SurrealDb/Models/NodeFeeSettlement.cs)
-- Guardrail: G6 SCHEMAFULL; durable, version-pinned fee-settlement intent
-- Note: An inert settlement boundary for fee consumers that require a separate treasury effect. The deterministic id is derived from the parent idempotency key and operation, while the row freezes pricing and treasury routing before either effect can be submitted.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $node_fee_settlement_operation VALUE ["Mint", "Transfer", "Swap", "QuestComplete", "FederationPublish"];
DEFINE PARAM IF NOT EXISTS $node_fee_settlement_network VALUE ["Devnet", "Testnet", "Mainnet"];
-- NodeFeeSettlement.StateKind
DEFINE PARAM IF NOT EXISTS $node_fee_settlement_state VALUE ["Prepared", "PrimarySubmitted", "FeeSubmitted", "AwaitingReconciliation", "Settled", "Cancelled"];
-- NodeFeeSettlement.EffectStateKind
DEFINE PARAM IF NOT EXISTS $node_fee_settlement_primary_effect_state VALUE ["NotStarted", "Submitted", "Confirmed", "Failed", "Unknown"];
-- NodeFeeSettlement.EffectStateKind
DEFINE PARAM IF NOT EXISTS $node_fee_settlement_fee_effect_state VALUE ["NotStarted", "Submitted", "Confirmed", "Failed", "Unknown"];

DEFINE TABLE IF NOT EXISTS node_fee_settlement SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE node_fee_settlement TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS parent_idempotency_key_hash ON TABLE node_fee_settlement TYPE string
    ASSERT string::matches($value, "^[0-9a-f]{64}$")
    READONLY;
DEFINE FIELD IF NOT EXISTS operation ON TABLE node_fee_settlement TYPE string
    ASSERT $value INSIDE $node_fee_settlement_operation
    READONLY;
DEFINE FIELD IF NOT EXISTS chain ON TABLE node_fee_settlement TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS network ON TABLE node_fee_settlement TYPE string
    ASSERT $value INSIDE $node_fee_settlement_network
    READONLY;
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE node_fee_settlement TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS gross_amount ON TABLE node_fee_settlement TYPE string
    ASSERT string::matches($value, "^[1-9][0-9]{0,19}$") AND type::decimal($value) <= 18446744073709551615dec
    READONLY;
DEFINE FIELD IF NOT EXISTS fee_amount ON TABLE node_fee_settlement TYPE string
    ASSERT string::matches($value, "^[1-9][0-9]{0,19}$") AND type::decimal($value) <= 18446744073709551615dec
    READONLY;
DEFINE FIELD IF NOT EXISTS net_amount ON TABLE node_fee_settlement TYPE string
    ASSERT string::matches($value, "^[1-9][0-9]{0,19}$") AND type::decimal($value) <= 18446744073709551615dec
    READONLY;
DEFINE FIELD IF NOT EXISTS fee_schedule_version ON TABLE node_fee_settlement TYPE int
    ASSERT $value >= 0
    DEFAULT 0
    READONLY;
DEFINE FIELD IF NOT EXISTS treasury_address ON TABLE node_fee_settlement TYPE string
    ASSERT $value != NONE AND $value != ""
    READONLY;
DEFINE FIELD IF NOT EXISTS treasury_destination_version ON TABLE node_fee_settlement TYPE int
    ASSERT $value >= 0
    DEFAULT 0
    READONLY;
DEFINE FIELD IF NOT EXISTS state ON TABLE node_fee_settlement TYPE string
    ASSERT $value INSIDE $node_fee_settlement_state
    DEFAULT "Prepared";
DEFINE FIELD IF NOT EXISTS primary_effect_state ON TABLE node_fee_settlement TYPE string
    ASSERT $value INSIDE $node_fee_settlement_primary_effect_state
    DEFAULT "NotStarted";
DEFINE FIELD IF NOT EXISTS fee_effect_state ON TABLE node_fee_settlement TYPE string
    ASSERT $value INSIDE $node_fee_settlement_fee_effect_state
    DEFAULT "NotStarted";
DEFINE FIELD IF NOT EXISTS primary_operation_id ON TABLE node_fee_settlement TYPE option<record<operation_log>>;
DEFINE FIELD IF NOT EXISTS fee_operation_id ON TABLE node_fee_settlement TYPE option<record<operation_log>>;
DEFINE FIELD IF NOT EXISTS primary_transaction_hash ON TABLE node_fee_settlement TYPE option<string>;
DEFINE FIELD IF NOT EXISTS fee_transaction_hash ON TABLE node_fee_settlement TYPE option<string>;
DEFINE FIELD IF NOT EXISTS state_version ON TABLE node_fee_settlement TYPE int
    ASSERT $value >= 0
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE node_fee_settlement TYPE int
    ASSERT $value >= 0
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS next_attempt_at ON TABLE node_fee_settlement TYPE datetime;
DEFINE FIELD IF NOT EXISTS lease_token ON TABLE node_fee_settlement TYPE option<string>;
DEFINE FIELD IF NOT EXISTS lease_expires_at ON TABLE node_fee_settlement TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS reconciliation_reason ON TABLE node_fee_settlement TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE node_fee_settlement TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE node_fee_settlement TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS node_fee_settlement_parent_operation
    ON TABLE node_fee_settlement
    FIELDS parent_idempotency_key_hash, operation
    UNIQUE;

DEFINE INDEX IF NOT EXISTS node_fee_settlement_recovery_due
    ON TABLE node_fee_settlement
    FIELDS state, next_attempt_at;

DEFINE INDEX IF NOT EXISTS node_fee_settlement_lease_expiry
    ON TABLE node_fee_settlement
    FIELDS lease_expires_at;
