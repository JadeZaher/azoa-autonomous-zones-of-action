-- ============================================================
-- Table: swap_state
-- Aggregate: Swap order/quote state (SwapQuoteRequest/SwapExecuteRequest/SwapQuoteResponse)
-- Guardrail: G6 SCHEMAFULL, G2 idempotency unique index
-- Note: Status uses OperationStatus constants; minimal durable state for replay + idempotency.
-- Note: B3 review: swap_state_idempotency_key UNIQUE on option<string>. Same NULL-collision caveat as bridge_tx: the swap-execute flow MUST supply an idempotency_key (api-safety-hardening §4 validator); NULL rows are diagnostic-only and the claim path never inserts NULL.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- SwapState.StatusKind
DEFINE PARAM IF NOT EXISTS $swap_state_status VALUE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "PendingConfirmation", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called"];

DEFINE TABLE IF NOT EXISTS swap_state SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE swap_state TYPE record<avatar>;
DEFINE FIELD IF NOT EXISTS chain ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";

-- Source/target token pair
DEFINE FIELD IF NOT EXISTS token_in ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS token_out ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";

-- Amounts (strings -- arbitrary precision)
DEFINE FIELD IF NOT EXISTS amount_in ON TABLE swap_state TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS expected_amount_out ON TABLE swap_state TYPE option<string>;
DEFINE FIELD IF NOT EXISTS actual_amount_out ON TABLE swap_state TYPE option<string>;

-- Slippage in basis points
DEFINE FIELD IF NOT EXISTS slippage_bps ON TABLE swap_state TYPE int
    DEFAULT 50;

-- Wallet executing the swap
DEFINE FIELD IF NOT EXISTS wallet_address ON TABLE swap_state TYPE option<string>;

-- Quote reference (chain-side opaque)
DEFINE FIELD IF NOT EXISTS quote_id ON TABLE swap_state TYPE option<string>;

-- Status (OperationStatus constants)
DEFINE FIELD IF NOT EXISTS status ON TABLE swap_state TYPE string
    ASSERT $value INSIDE $swap_state_status
    DEFAULT "Pending";

-- G2 idempotency
DEFINE FIELD IF NOT EXISTS idempotency_key ON TABLE swap_state TYPE option<string>;
DEFINE FIELD IF NOT EXISTS error_message ON TABLE swap_state TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE swap_state TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE swap_state TYPE datetime;
DEFINE FIELD IF NOT EXISTS completed_at ON TABLE swap_state TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS swap_state_idempotency_key
    ON TABLE swap_state
    FIELDS idempotency_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS swap_state_avatar_id
    ON TABLE swap_state
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS swap_state_status
    ON TABLE swap_state
    FIELDS status;
