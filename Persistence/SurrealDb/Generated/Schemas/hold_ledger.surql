-- ============================================================
-- Table: hold_ledger
-- Aggregate: HoldCommand (Services/Hold/HoldCommandService.cs)
-- Guardrail: G6 SCHEMAFULL; protocol, network, owner and tenant qualified custody liability
-- Note: Immutable definition and opening command bind liability; atomic command receipts preserve replay independently of later state. See Services/Hold/AGENTS.md.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $hold_ledger_network VALUE ["Devnet", "Testnet", "Mainnet"];
DEFINE PARAM IF NOT EXISTS $hold_ledger_funding_source VALUE ["Reserve", "Funder"];
DEFINE PARAM IF NOT EXISTS $hold_ledger_state VALUE ["Pending", "AwaitingSourceSettlement", "Held", "Releasing", "Released", "Refunded", "ReleasedIntoHold", "Cancelled", "PartiallyRecovered"];

DEFINE TABLE IF NOT EXISTS hold_ledger SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS protocol_identity ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS owner_user_id ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS definition_hash ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS definition_json ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS opening_command_id ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS active_command_id ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS active_release_mandate_id ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS economic_source_id ON TABLE hold_ledger TYPE option<string>;

-- Core custody identity
DEFINE FIELD IF NOT EXISTS id ON TABLE hold_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS hold_id ON TABLE hold_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS chain ON TABLE hold_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS network ON TABLE hold_ledger TYPE string
    ASSERT $value INSIDE $hold_ledger_network;

-- What is held, where, and out of which declared budget
DEFINE FIELD IF NOT EXISTS holding_address ON TABLE hold_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE hold_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS amount ON TABLE hold_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS reserve_partition ON TABLE hold_ledger TYPE string;
DEFINE FIELD IF NOT EXISTS funding_source ON TABLE hold_ledger TYPE string
    ASSERT $value INSIDE $hold_ledger_funding_source;

-- Who it is for
DEFINE FIELD IF NOT EXISTS funder_user_id ON TABLE hold_ledger TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS beneficiary_user_id ON TABLE hold_ledger TYPE record<user>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE hold_ledger TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS purpose ON TABLE hold_ledger TYPE option<string>;

-- Lifecycle
DEFINE FIELD IF NOT EXISTS state ON TABLE hold_ledger TYPE string
    ASSERT $value INSIDE $hold_ledger_state;
DEFINE FIELD IF NOT EXISTS release_not_before ON TABLE hold_ledger TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS auto_release ON TABLE hold_ledger TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS inherits_reserve_commitment ON TABLE hold_ledger TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS parent_hold_id ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS child_hold_id ON TABLE hold_ledger TYPE option<string>;

-- Rail evidence and settlement
DEFINE FIELD IF NOT EXISTS settlement_transaction_id ON TABLE hold_ledger TYPE option<string>;
DEFINE FIELD IF NOT EXISTS settled_at ON TABLE hold_ledger TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS settlement_reason ON TABLE hold_ledger TYPE option<string>;

-- Optimistic concurrency + timestamps
DEFINE FIELD IF NOT EXISTS version ON TABLE hold_ledger TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE hold_ledger TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE hold_ledger TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS hold_ledger_hold
    ON TABLE hold_ledger
    FIELDS chain, network, protocol_identity, owner_user_id, tenant_id, hold_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS hold_ledger_due
    ON TABLE hold_ledger
    FIELDS chain, network, state, release_not_before;

DEFINE INDEX IF NOT EXISTS hold_ledger_beneficiary
    ON TABLE hold_ledger
    FIELDS beneficiary_user_id;

DEFINE INDEX IF NOT EXISTS hold_ledger_funder
    ON TABLE hold_ledger
    FIELDS funder_user_id;
