-- ============================================================
-- Table: fiat_payout
-- Aggregate: OffRampSaga (Services/Fiat/OffRamp/OffRampService.cs)
-- Guardrail: G6 SCHEMAFULL; one row per cash-out request and it is the off-ramp's idempotency anchor
-- Note: One row is ONE cash-out: this many units left this person's account and became this much money in their own bank account. It is the mirror of on_ramp_lot and the only durable record of the outbound half of the loop -- the node keeps no balance ledger of its own (D47), so nothing on chain can say which payout a transfer belongs to.
-- Note: The record id is a deterministic Guid derived from subject|request key, so a double-tapped 'cash out' button settles into ONE row. It is a compact Guid rather than the SHA-256 hex on_ramp_lot uses because it is stamped into provider metadata as azoa_intent_id, and the webhook envelope only recovers a correlation that parses as a compact Guid -- that is how transfer.reversed finds its way back to this row without a second lookup table.
-- Note: status is the saga: Requested (claimed, nothing has moved), UnitsCollected (the units are back in the reserve and the money has NOT been sent -- the one state where the person is out of units and has no money), BankPending (the connected account is funded and the bank payout is pending), Paid (the bank payout is confirmed paid by the provider), Reversed (the provider pulled the money back out again), Returned (the cash-out was abandoned and the units were given back), Refused (refused before anything moved), NeedsOperator (only a person can move this on, and the reason is on the row).
-- Note: quantity_base_units is what the person gives up and amount_minor_units is what they are paid, both derived from the SAME registered price schedule the on-ramp sells at (tenant_price_schedule). Storing units_per_minor_unit and price_schedule_version on the row is what keeps the arithmetic re-derivable after that schedule is retired.
-- Note: consumed_lots is the FIFO receipt: which on_ramp_lot rows this cash-out drew down, and by how much. It exists so an abandoned cash-out can hand the provenance back rather than silently ageing units that were never actually cashed out. Units with NO lot -- units received from selling work or stake -- are unaged and appear here not at all (Q-X2).
-- Note: units_owed_base_units is the honest record of the worst case: the units were collected, the money never left, and giving the units back also failed. The row parks at NeedsOperator carrying the figure instead of the node quietly keeping units it was paid nothing for.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $fiat_payout_status VALUE ["Requested", "UnitsCollected", "BankPending", "Paid", "Reversed", "Returned", "Refused", "NeedsOperator"];

DEFINE TABLE IF NOT EXISTS fiat_payout SCHEMAFULL;

-- Core identity (record id is a deterministic Guid over subject + request key)
DEFINE FIELD IF NOT EXISTS id ON TABLE fiat_payout TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE fiat_payout TYPE record<user>;
DEFINE FIELD IF NOT EXISTS request_key ON TABLE fiat_payout TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fiat_payout TYPE option<record<user>>;

-- What leaves the account, and what is paid for it
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE fiat_payout TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE fiat_payout TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS quantity_base_units ON TABLE fiat_payout TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS amount_minor_units ON TABLE fiat_payout TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE fiat_payout TYPE string
    ASSERT $value != NONE AND $value != "";

-- The registered price that converted the units (the same schedule the on-ramp sells at)
DEFINE FIELD IF NOT EXISTS price_schedule_id ON TABLE fiat_payout TYPE option<record<tenant_price_schedule>>;
DEFINE FIELD IF NOT EXISTS price_schedule_version ON TABLE fiat_payout TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS units_per_minor_unit ON TABLE fiat_payout TYPE string
    DEFAULT "0";

-- Source-aged provenance: the on-ramp lots this cash-out consumed, FIFO
DEFINE FIELD IF NOT EXISTS consumed_lots ON TABLE fiat_payout TYPE option<object> FLEXIBLE;
DEFINE FIELD IF NOT EXISTS aged_quantity_base_units ON TABLE fiat_payout TYPE string
    DEFAULT "0";

-- Provider-side provenance the reversal pipeline anchors on
DEFINE FIELD IF NOT EXISTS connected_account_id ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS provider_transfer_id ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS units_collected_transaction_id ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS units_returned_transaction_id ON TABLE fiat_payout TYPE option<string>;

-- Lifecycle
DEFINE FIELD IF NOT EXISTS status ON TABLE fiat_payout TYPE string
    ASSERT $value INSIDE $fiat_payout_status
    DEFAULT "Requested";
DEFINE FIELD IF NOT EXISTS status_reason ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS units_owed_base_units ON TABLE fiat_payout TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS reversed_minor_units ON TABLE fiat_payout TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS reversal_id ON TABLE fiat_payout TYPE option<record<fiat_reversal>>;
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE fiat_payout TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS requested_at ON TABLE fiat_payout TYPE datetime;
DEFINE FIELD IF NOT EXISTS units_collected_at ON TABLE fiat_payout TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS paid_at ON TABLE fiat_payout TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS reversed_at ON TABLE fiat_payout TYPE option<datetime>;

-- Optimistic concurrency + timestamps
DEFINE FIELD IF NOT EXISTS version ON TABLE fiat_payout TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fiat_payout TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE fiat_payout TYPE datetime;
DEFINE FIELD IF NOT EXISTS provider_payout_id ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS bank_payout_started_at ON TABLE fiat_payout TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS bank_payout_attempt ON TABLE fiat_payout TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS environment ON TABLE fiat_payout TYPE string
    DEFAULT ALWAYS "unbound";
DEFINE FIELD IF NOT EXISTS chain_network ON TABLE fiat_payout TYPE string
    DEFAULT ALWAYS "unbound";
DEFINE FIELD IF NOT EXISTS recovery_requested ON TABLE fiat_payout TYPE bool
    DEFAULT ALWAYS false;
DEFINE FIELD IF NOT EXISTS recovery_reversal_amount ON TABLE fiat_payout TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS recovery_started_at ON TABLE fiat_payout TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS provider_transfer_reversal_id ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS restored_quantity_base_units ON TABLE fiat_payout TYPE string
    DEFAULT ALWAYS "0";
DEFINE FIELD IF NOT EXISTS restitution_key ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS restitution_target_base_units ON TABLE fiat_payout TYPE string
    DEFAULT ALWAYS "0";
DEFINE FIELD IF NOT EXISTS recovery_audit_json ON TABLE fiat_payout TYPE string
    DEFAULT ALWAYS "[]";
DEFINE FIELD IF NOT EXISTS return_pending_transaction_id ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS return_pending_idempotency_key ON TABLE fiat_payout TYPE option<string>;
DEFINE FIELD IF NOT EXISTS recovery_reversal_attempt ON TABLE fiat_payout TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS funding_tenant_id ON TABLE fiat_payout TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS collect_partition ON TABLE fiat_payout TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fiat_payout_request
    ON TABLE fiat_payout
    FIELDS subject_user_id, request_key, environment, chain_network
    UNIQUE;

DEFINE INDEX IF NOT EXISTS fiat_payout_account
    ON TABLE fiat_payout
    FIELDS subject_user_id, status;

DEFINE INDEX IF NOT EXISTS fiat_payout_transfer
    ON TABLE fiat_payout
    FIELDS provider_transfer_id;

DEFINE INDEX IF NOT EXISTS fiat_payout_board
    ON TABLE fiat_payout
    FIELDS status, requested_at;
