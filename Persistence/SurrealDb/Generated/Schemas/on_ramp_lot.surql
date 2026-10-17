-- ============================================================
-- Table: on_ramp_lot
-- Aggregate: OnRampSaga (Services/Fiat/OnRamp/OnRampSettlementHandler.cs)
-- Guardrail: G6 SCHEMAFULL; one row per funding intent and it is the on-ramp's idempotency anchor
-- Note: One row is ONE fiat-funded issuance: this much money became this many units for this person at this price, out of this card payment. The units themselves are a FUNGIBLE asset on chain, so the chain cannot say which of a person's units were bought with a card and which were earned -- and the node keeps no balance ledger of its own (D47). This table is the only per-lot provenance there is, which is what the source-aged cash-out rule (W8.1) consumes FIFO. Units received from selling work or stake have NO lot and are therefore unaged.
-- Note: The record id is the SHA-256 of the funding intent id, so one intent can settle into at most one lot no matter how many times the provider redelivers. checkout.session.completed and a later async_payment_succeeded for the same session address the same row.
-- Note: status is the saga: Pending (money cleared, units not yet confirmed), Issued (units are in the person's account), Refused (the node decided not to credit -- the money is owed back and the reversal pipeline owes a refund), Stuck (the node tried attempt_count times and could not determine the outcome; reconcile before refunding or crediting), Reversed (W4.3 clawed it back). Refused and Stuck exist so the provider can be told to stop retrying WITHOUT the charge becoming invisible.
-- Note: fiat_minor_units and currency_code are the real money the provider confirmed, re-read server-side. Together with price_schedule_version and units_per_minor_unit they make the quantity re-derivable: this is the D16 value of the issuance stated honestly, because on the on-ramp the fiat amount is actually known rather than modelled.
-- Note: quantity_base_units is what the person RECEIVED (net of the node fee); gross_quantity_base_units is what the price schedule produced and node_fee_base_units is the difference. W8.1 ages and consumes the net figure -- crediting the gross would let a cash-out draw units that were never issued.
-- Note: consumed_base_units is W8.1's FIFO watermark. It lives here rather than in a separate table so a partially cashed-out lot cannot disagree with itself; version is the compare-and-swap anchor every writer of this row uses.
-- Note: reversal_window_ends is issued_at plus the RAIL's declared chargeback window (Core/SettlementProfile.cs), not a constant chosen here. Until it passes these units are spendable inside the platform but are not cashable out, because the money that bought them can still be taken back.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $on_ramp_lot_status VALUE ["Pending", "Issued", "Refused", "Stuck", "Reversed", "RefundPending", "Refunded"];

DEFINE TABLE IF NOT EXISTS on_ramp_lot SCHEMAFULL;

-- Core identity (record id is the SHA-256 of the funding intent this lot settles)
DEFINE FIELD IF NOT EXISTS id ON TABLE on_ramp_lot TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS intent_id ON TABLE on_ramp_lot TYPE record<hosted_session>;
DEFINE FIELD IF NOT EXISTS subject_user_id ON TABLE on_ramp_lot TYPE record<user>;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE on_ramp_lot TYPE option<record<user>>;

-- What was issued, and out of what money
DEFINE FIELD IF NOT EXISTS chain_type ON TABLE on_ramp_lot TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE on_ramp_lot TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS quantity_base_units ON TABLE on_ramp_lot TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS gross_quantity_base_units ON TABLE on_ramp_lot TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS node_fee_base_units ON TABLE on_ramp_lot TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS consumed_base_units ON TABLE on_ramp_lot TYPE string
    DEFAULT "0";
DEFINE FIELD IF NOT EXISTS fiat_minor_units ON TABLE on_ramp_lot TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS currency_code ON TABLE on_ramp_lot TYPE string
    ASSERT $value != NONE AND $value != "";

-- The registered price that converted the money (never a client-supplied quantity)
DEFINE FIELD IF NOT EXISTS price_schedule_id ON TABLE on_ramp_lot TYPE option<record<tenant_price_schedule>>;
DEFINE FIELD IF NOT EXISTS price_schedule_version ON TABLE on_ramp_lot TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS units_per_minor_unit ON TABLE on_ramp_lot TYPE string
    DEFAULT "0";

-- Provider-side provenance the reversal pipeline anchors on
DEFINE FIELD IF NOT EXISTS source_checkout_session_id ON TABLE on_ramp_lot TYPE option<string>;
DEFINE FIELD IF NOT EXISTS source_payment_intent_id ON TABLE on_ramp_lot TYPE option<string>;
DEFINE FIELD IF NOT EXISTS source_charge_id ON TABLE on_ramp_lot TYPE option<string>;

-- Lifecycle
DEFINE FIELD IF NOT EXISTS status ON TABLE on_ramp_lot TYPE string
    ASSERT $value INSIDE $on_ramp_lot_status
    DEFAULT "Pending";
DEFINE FIELD IF NOT EXISTS status_reason ON TABLE on_ramp_lot TYPE option<string>;
DEFINE FIELD IF NOT EXISTS operation_id ON TABLE on_ramp_lot TYPE option<record<operation_log>>;
DEFINE FIELD IF NOT EXISTS attempt_count ON TABLE on_ramp_lot TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS issued_at ON TABLE on_ramp_lot TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS reversal_window_ends ON TABLE on_ramp_lot TYPE option<datetime>;

-- Optimistic concurrency + timestamps
DEFINE FIELD IF NOT EXISTS version ON TABLE on_ramp_lot TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE on_ramp_lot TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE on_ramp_lot TYPE datetime;
DEFINE FIELD IF NOT EXISTS provider_refund_id ON TABLE on_ramp_lot TYPE option<string>;
DEFINE FIELD IF NOT EXISTS refund_started_at ON TABLE on_ramp_lot TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS recovery_audit_json ON TABLE on_ramp_lot TYPE string
    DEFAULT ALWAYS "[]";
DEFINE FIELD IF NOT EXISTS environment ON TABLE on_ramp_lot TYPE string
    DEFAULT ALWAYS "unbound";
DEFINE FIELD IF NOT EXISTS chain_network ON TABLE on_ramp_lot TYPE string
    DEFAULT ALWAYS "unbound";
DEFINE FIELD IF NOT EXISTS refund_attempt ON TABLE on_ramp_lot TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS refunded_minor_units ON TABLE on_ramp_lot TYPE int
    DEFAULT ALWAYS 0;
DEFINE FIELD IF NOT EXISTS provider_refund_status ON TABLE on_ramp_lot TYPE option<string>;
DEFINE FIELD IF NOT EXISTS reversed_base_units ON TABLE on_ramp_lot TYPE string
    DEFAULT ALWAYS "0";
DEFINE FIELD IF NOT EXISTS reversal_marks_json ON TABLE on_ramp_lot TYPE string
    DEFAULT ALWAYS "{}";

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS on_ramp_lot_intent
    ON TABLE on_ramp_lot
    FIELDS intent_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS on_ramp_lot_fifo
    ON TABLE on_ramp_lot
    FIELDS subject_user_id, status, issued_at;

DEFINE INDEX IF NOT EXISTS on_ramp_lot_payment
    ON TABLE on_ramp_lot
    FIELDS source_payment_intent_id;

DEFINE INDEX IF NOT EXISTS on_ramp_lot_charge
    ON TABLE on_ramp_lot
    FIELDS source_charge_id;
