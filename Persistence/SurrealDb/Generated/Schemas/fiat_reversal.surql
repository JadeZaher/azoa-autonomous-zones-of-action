-- ============================================================
-- Table: fiat_reversal
-- Aggregate: FiatReversalSaga (Services/Fiat/Reversal/FiatReversalEventHandler.cs)
-- Guardrail: G6 SCHEMAFULL; one row per reversed payment and it is the compensation saga's anchor
-- Note: One row is ONE reversal of one card payment: a refund the node issued, or a dispute the payer raised. The four provider events the pipeline handles -- charge.refunded, charge.dispute.created, charge.dispute.funds_withdrawn and charge.dispute.funds_reinstated -- each land as their own fiat_event inbox row and then MOVE this row. That split is deliberate: fiat_event is the per-delivery evidence, this table is the standing fact of what the node owes back and what it managed to recover.
-- Note: The record id is the SHA-256 of provider|scope|provider_object_id, where scope is 'refund' or 'dispute'. A charge that is both partially refunded AND disputed is therefore two reversals, while every redelivery of one dispute's lifecycle addresses one row. A second partial refund of the same charge re-reads the provider's CUMULATIVE amount_refunded onto the same row, so partial refunds accumulate instead of racing.
-- Note: amount_minor_units is the money taken back, re-read from the provider server-side. It is NEVER parsed from the webhook body: a body-supplied reversal amount would be the money-printer of the on-ramp read backwards -- claw back more units than the payment ever bought. source_amount_minor_units is the original payment, and the two together are what make a PARTIAL reversal an exact proportion rather than an estimate.
-- Note: funds_withdrawn separates 'a dispute exists' from 'the money is gone'. charge.dispute.created opens the row and (per the flow's risk policy) flags the account, but recovers nothing: clawing units back over a dispute that may still be resolved in the node's favour punishes a person for asking a question. charge.dispute.funds_withdrawn is what actually starts the compensation saga.
-- Note: recovery_path records WHICH rung of the compensation ladder answered: Balance (the units were still in the payer's account), Hold (an open hold their money had funded was refunded), Purchase (the units they had bought were clawed back), Reserve (nothing was recoverable and the platform absorbed the loss), Mixed (more than one rung contributed). None means no rung has run yet. Absorbed loss is recorded, never hidden -- absorbed_quantity_base_units is the node's own bill.
-- Note: account_flagged is a CONTROL, not a note: RiskHoldValueGate (Services/Fiat/Reversal/RiskHoldValueGate.cs) refuses the D46 value gate for any account carrying an unresolved flag, so a flagged payer cannot buy more units while the node is still out of pocket. Clearing it is an operator action and is stamped with who cleared it.
-- Note: reinstatement_owed_base_units is the honest record of the awkward case: the node clawed units back, then the provider reinstated the disputed funds. The node now owes those units again, and re-issuing them is the on-ramp's job rather than this pipeline's, so the row parks at NeedsOperator with the figure on it instead of silently keeping the units.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $fiat_reversal_kind VALUE ["Refund", "Dispute"];
DEFINE PARAM IF NOT EXISTS $fiat_reversal_recovery_path VALUE ["None", "Balance", "Hold", "Purchase", "Reserve", "Mixed"];
DEFINE PARAM IF NOT EXISTS $fiat_reversal_status VALUE ["Open", "Recovering", "Recovered", "PartiallyRecovered", "Absorbed", "Reinstated", "Unmatched", "NeedsOperator"];

DEFINE TABLE IF NOT EXISTS fiat_reversal SCHEMAFULL;

-- Core identity (record id is the SHA-256 of provider|scope|provider object id)
DEFINE FIELD IF NOT EXISTS id ON TABLE fiat_reversal TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS provider ON TABLE fiat_reversal TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS provider_object_id ON TABLE fiat_reversal TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS kind ON TABLE fiat_reversal TYPE string
    ASSERT $value INSIDE $fiat_reversal_kind
    DEFAULT "Refund";
DEFINE FIELD IF NOT EXISTS last_event_type ON TABLE fiat_reversal TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS provider_event_id ON TABLE fiat_reversal TYPE string
    ASSERT $value != NONE AND $value != "";

-- Correlation back to the payment this unwinds
DEFINE FIELD IF NOT EXISTS charge_id ON TABLE fiat_reversal TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS payment_intent_id ON TABLE fiat_reversal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS checkout_session_id ON TABLE fiat_reversal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS intent_id ON TABLE fiat_reversal TYPE option<record<hosted_session>>;
DEFINE FIELD IF NOT EXISTS lot_id ON TABLE fiat_reversal TYPE option<record<on_ramp_lot>>;
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE fiat_reversal TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE fiat_reversal TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS flow_purpose ON TABLE fiat_reversal TYPE option<string>;

-- The money taken back, re-read from the provider (never the webhook body)
DEFINE FIELD IF NOT EXISTS amount_minor_units ON TABLE fiat_reversal TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE fiat_reversal TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS source_amount_minor_units ON TABLE fiat_reversal TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS is_partial ON TABLE fiat_reversal TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS funds_withdrawn ON TABLE fiat_reversal TYPE bool
    DEFAULT false;

-- What has to be unwound, and what actually was
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE fiat_reversal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE fiat_reversal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS target_quantity_base_units ON TABLE fiat_reversal TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS recovered_quantity_base_units ON TABLE fiat_reversal TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS absorbed_quantity_base_units ON TABLE fiat_reversal TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS reinstatement_owed_base_units ON TABLE fiat_reversal TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS recovery_path ON TABLE fiat_reversal TYPE string
    ASSERT $value INSIDE $fiat_reversal_recovery_path
    DEFAULT "None";
DEFINE FIELD IF NOT EXISTS recovery_transaction_id ON TABLE fiat_reversal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE fiat_reversal TYPE option<record<operation_log>>;

-- Lifecycle
DEFINE FIELD IF NOT EXISTS status ON TABLE fiat_reversal TYPE string
    ASSERT $value INSIDE $fiat_reversal_status
    DEFAULT "Open";
DEFINE FIELD IF NOT EXISTS status_reason ON TABLE fiat_reversal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE fiat_reversal TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS opened_at ON TABLE fiat_reversal TYPE datetime;
DEFINE FIELD IF NOT EXISTS resolved_at ON TABLE fiat_reversal TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS reinstated_at ON TABLE fiat_reversal TYPE option<datetime>;

-- Risk flag on the account (the value gate is the read side)
DEFINE FIELD IF NOT EXISTS account_flagged ON TABLE fiat_reversal TYPE bool
    DEFAULT false;
DEFINE FIELD IF NOT EXISTS flag_reason ON TABLE fiat_reversal TYPE option<string>;
DEFINE FIELD IF NOT EXISTS flag_cleared_at ON TABLE fiat_reversal TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS flag_cleared_by ON TABLE fiat_reversal TYPE option<record<user>>;

-- Optimistic concurrency + timestamps
DEFINE FIELD IF NOT EXISTS version ON TABLE fiat_reversal TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE fiat_reversal TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE fiat_reversal TYPE datetime;
DEFINE FIELD IF NOT EXISTS purchase_recovered_base_units ON TABLE fiat_reversal TYPE string
    DEFAULT ALWAYS "0";

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS fiat_reversal_object
    ON TABLE fiat_reversal
    FIELDS provider, provider_object_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS fiat_reversal_charge
    ON TABLE fiat_reversal
    FIELDS charge_id;

DEFINE INDEX IF NOT EXISTS fiat_reversal_open
    ON TABLE fiat_reversal
    FIELDS status, opened_at;

DEFINE INDEX IF NOT EXISTS fiat_reversal_lot
    ON TABLE fiat_reversal
    FIELDS lot_id;

DEFINE INDEX IF NOT EXISTS fiat_reversal_flag
    ON TABLE fiat_reversal
    FIELDS subject_user_id, account_flagged;
