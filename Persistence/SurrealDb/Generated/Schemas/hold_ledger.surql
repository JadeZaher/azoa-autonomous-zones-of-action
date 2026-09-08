-- ============================================================
-- Table: hold_ledger
-- Aggregate: PlatformHold (Services/Hold/PlatformHoldService.cs)
-- Guardrail: G6 SCHEMAFULL; one row per (chain, network, hold id) and it is the idempotency anchor
-- Note: One row is ONE hold: units committed in the platform-custodial holding account for a named beneficiary until release_not_before elapses. The row -- not a caller-supplied header -- is the idempotency anchor, so a retried open, release or refund reads the state it already reached instead of moving value twice.
-- Note: holding_address is the operator-owned custodial account the units sit in, frozen at open. It is the platform reserve account: the reserve is the manager/reserve authority on every asset this node creates, so it can already hold them and no separate holding account has to be funded and made able to accept each asset. A hold is REFUSED for an asset the reserve has no ledger row for.
-- Note: state is a strict machine: Pending -> Held -> Releasing -> Released, Pending|Held -> Refunded, and Held -> ReleasedIntoHold. Releasing exists because the reserve partition draw commits BEFORE the outbound move; a row parked there is one whose units are already counted as issued, so a refund from it is refused rather than double-counted.
-- Note: release_not_before is the hold window (Q5). A plain release before it is refused; a release INTO a second hold is allowed early because no value leaves the holding account -- but the child window may never be earlier than the parent's, or chaining would be a way out of the funder's window.
-- Note: inherits_reserve_commitment marks a hold that took NO partition draw of its own because its parent already did. Exactly one row in a parent/child chain performs the release-hold or cancel-hold draw, which is what keeps the reserve counters from double-counting a single commitment.
-- Note: settlement_transaction_id is written as soon as the rail returns it. A retried release reads it first and reconciles against chain truth before re-sending, so a broadcast-then-timeout cannot become a second payment.
-- Note: version is the compare-and-swap anchor. Every transition reads the row, decides in process, and writes back only while version is unchanged, so two concurrent releases cannot both settle one hold.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $hold_ledger_network VALUE ["Devnet", "Testnet", "Mainnet"];
DEFINE PARAM IF NOT EXISTS $hold_ledger_funding_source VALUE ["Reserve", "Funder"];
DEFINE PARAM IF NOT EXISTS $hold_ledger_state VALUE ["Pending", "Held", "Releasing", "Released", "Refunded", "ReleasedIntoHold"];

DEFINE TABLE IF NOT EXISTS hold_ledger SCHEMAFULL;

-- Core identity (record id is the SHA-256 of chain|network|hold id)
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
    FIELDS chain, network, hold_id
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
