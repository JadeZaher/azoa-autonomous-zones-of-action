-- ============================================================
-- Table: swap_state
-- Aggregate: Swap order/quote state (SwapQuoteRequest/SwapExecuteRequest/SwapQuoteResponse)
-- Guardrail: G6 SCHEMAFULL, G2 idempotency unique index
-- Note: Status uses OperationStatus constants; minimal durable state for replay + idempotency.
-- Note: B3 review: swap_state_idempotency_key UNIQUE on option<string>. Same NULL-collision caveat as bridge_tx: the swap-execute flow MUST supply an idempotency_key (api-safety-hardening §4 validator); NULL rows are diagnostic-only and the claim path never inserts NULL.
-- ============================================================

DEFINE TABLE swap_state SCHEMAFULL;

DEFINE FIELD id ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD avatar_id ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD chain ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";

-- Source/target token pair
DEFINE FIELD token_in ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD token_out ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";

-- Amounts (strings -- arbitrary precision)
DEFINE FIELD amount_in ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD expected_amount_out ON TABLE swap_state TYPE option<string>;
DEFINE FIELD actual_amount_out ON TABLE swap_state TYPE option<string>;

-- Slippage in basis points
DEFINE FIELD slippage_bps ON TABLE swap_state TYPE int
    DEFAULT 50;

-- Wallet executing the swap
DEFINE FIELD wallet_address ON TABLE swap_state TYPE option<string>;

-- Quote reference (chain-side opaque)
DEFINE FIELD quote_id ON TABLE swap_state TYPE option<string>;

-- Status (OperationStatus constants)
DEFINE FIELD status ON TABLE swap_state TYPE string
    ASSERT $value INSIDE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called"]
    DEFAULT "Pending";

-- G2 idempotency
DEFINE FIELD idempotency_key ON TABLE swap_state TYPE option<string>;
DEFINE FIELD error_message ON TABLE swap_state TYPE option<string>;
DEFINE FIELD created_at ON TABLE swap_state TYPE datetime;
DEFINE FIELD updated_at ON TABLE swap_state TYPE datetime;
DEFINE FIELD completed_at ON TABLE swap_state TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX swap_state_idempotency_key
    ON TABLE swap_state
    FIELDS idempotency_key
    UNIQUE;

DEFINE INDEX swap_state_avatar_id
    ON TABLE swap_state
    FIELDS avatar_id;

DEFINE INDEX swap_state_status
    ON TABLE swap_state
    FIELDS status;
