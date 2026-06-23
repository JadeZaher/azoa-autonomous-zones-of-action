-- ============================================================
-- Table: operation_log
-- Aggregate: BlockchainOperation (Models/BlockchainOperation.cs)
-- Guardrail: G6 SCHEMAFULL, G2 idempotency unique index
-- Note: Status values from OperationStatus constants; Parameters is Dictionary<string,string> stored as object.
-- Note: B3 review: operation_log_idempotency_key UNIQUE on option<string>. NULL rows do NOT collide -- adapter MUST supply a non-NULL key on the claim path; api-safety-hardening §4 validator enforces this end-to-end.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- OperationLog.StatusKind
DEFINE PARAM IF NOT EXISTS $operation_log_status VALUE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called"];

DEFINE TABLE IF NOT EXISTS operation_log SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE operation_log TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE operation_log TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS wallet_id ON TABLE operation_log TYPE option<record<wallet>>;
DEFINE FIELD IF NOT EXISTS operation_type ON TABLE operation_log TYPE string
    ASSERT $value != NONE AND $value != "";

-- Status (OperationStatus closed set)
DEFINE FIELD IF NOT EXISTS status ON TABLE operation_log TYPE string
    ASSERT $value INSIDE $operation_log_status
    DEFAULT "Pending";

-- Opaque parameters bag (Dictionary<string,string>)
DEFINE FIELD IF NOT EXISTS parameters ON TABLE operation_log TYPE option<object>;

-- IMintOperation fields
DEFINE FIELD IF NOT EXISTS token_uri ON TABLE operation_log TYPE option<string>;
DEFINE FIELD IF NOT EXISTS amount ON TABLE operation_log TYPE option<int>;
DEFINE FIELD IF NOT EXISTS asset_type ON TABLE operation_log TYPE option<string>;

-- IExchangeOperation fields
DEFINE FIELD IF NOT EXISTS source_holon_id ON TABLE operation_log TYPE option<record<holon>>;
DEFINE FIELD IF NOT EXISTS target_holon_id ON TABLE operation_log TYPE option<record<holon>>;
DEFINE FIELD IF NOT EXISTS exchange_rate ON TABLE operation_log TYPE option<string>;

-- ITransferOperation fields
DEFINE FIELD IF NOT EXISTS recipient_address ON TABLE operation_log TYPE option<string>;

-- G2 idempotency
DEFINE FIELD IF NOT EXISTS idempotency_key ON TABLE operation_log TYPE option<string>;

-- Error detail (populated on failure)
DEFINE FIELD IF NOT EXISTS error ON TABLE operation_log TYPE option<string>;

-- tenant-consent-delegation AC4: acting tenant + required signing scope
DEFINE FIELD IF NOT EXISTS acting_tenant_id ON TABLE operation_log TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS signing_scope ON TABLE operation_log TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_date ON TABLE operation_log TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS completed_date ON TABLE operation_log TYPE option<datetime>;

-- Holon-asset link (economic-primitive-nodes)
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE operation_log TYPE option<string>;
DEFINE FIELD IF NOT EXISTS tx_hash ON TABLE operation_log TYPE option<string>;
DEFINE FIELD IF NOT EXISTS holon_id ON TABLE operation_log TYPE option<record<holon>>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS operation_log_idempotency_key
    ON TABLE operation_log
    FIELDS idempotency_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS operation_log_avatar_created
    ON TABLE operation_log
    FIELDS avatar_id, created_date;

DEFINE INDEX IF NOT EXISTS operation_log_status
    ON TABLE operation_log
    FIELDS status;
