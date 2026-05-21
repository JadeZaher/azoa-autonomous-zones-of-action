-- ============================================================
-- Table: operation_log
-- Aggregate: BlockchainOperation (Models/BlockchainOperation.cs)
-- Guardrail: G6 SCHEMAFULL, G2 idempotency unique index
-- Note: Status values from OperationStatus constants; Parameters is Dictionary<string,string> stored as object.
-- ============================================================

DEFINE TABLE operation_log SCHEMAFULL;

DEFINE FIELD id ON TABLE operation_log TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD avatar_id ON TABLE operation_log TYPE option<string>;
DEFINE FIELD wallet_id ON TABLE operation_log TYPE option<string>;
DEFINE FIELD operation_type ON TABLE operation_log TYPE string
    ASSERT $value != NONE AND $value != "";

-- Status (OperationStatus closed set)
DEFINE FIELD status ON TABLE operation_log TYPE string
    ASSERT $value INSIDE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called"]
    DEFAULT "Pending";

-- Opaque parameters bag (Dictionary<string,string>)
DEFINE FIELD parameters ON TABLE operation_log TYPE option<object>;

-- IMintOperation fields
DEFINE FIELD token_uri ON TABLE operation_log TYPE option<string>;
DEFINE FIELD amount ON TABLE operation_log TYPE option<int>;
DEFINE FIELD asset_type ON TABLE operation_log TYPE option<string>;

-- IExchangeOperation fields
DEFINE FIELD source_holon_id ON TABLE operation_log TYPE option<string>;
DEFINE FIELD target_holon_id ON TABLE operation_log TYPE option<string>;
DEFINE FIELD exchange_rate ON TABLE operation_log TYPE option<string>;

-- ITransferOperation fields
DEFINE FIELD recipient_address ON TABLE operation_log TYPE option<string>;

-- G2 idempotency
DEFINE FIELD idempotency_key ON TABLE operation_log TYPE option<string>;

-- Error detail (populated on failure)
DEFINE FIELD error ON TABLE operation_log TYPE option<string>;

-- Timestamps
DEFINE FIELD created_date ON TABLE operation_log TYPE datetime;
DEFINE FIELD completed_date ON TABLE operation_log TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX operation_log_idempotency_key
    ON TABLE operation_log
    FIELDS idempotency_key
    UNIQUE;

DEFINE INDEX operation_log_avatar_created
    ON TABLE operation_log
    FIELDS avatar_id, created_date;

DEFINE INDEX operation_log_status
    ON TABLE operation_log
    FIELDS status;
