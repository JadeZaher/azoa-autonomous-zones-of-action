-- ============================================================
-- Table: operation_log
-- Aggregate: BlockchainOperation (Models/BlockchainOperation.cs)
-- Guardrail: G6 SCHEMAFULL, G2 idempotency unique index
-- Note: Status values from OperationStatus constants; Parameters is Dictionary<string,string> stored as object.
-- Note: D-3: NeedsOperator parks a client-signed transfer the chain cannot settle on its own (an unexpired Ethereum signature); see Services/Blockchain/AGENTS.md.
-- Note: B3 review: operation_log_idempotency_key UNIQUE on option<string>. NONE rows do NOT collide (verified on SurrealDB 3.1.4) -- adapter MUST supply a non-NONE key on the claim path; api-safety-hardening §4 validator enforces this end-to-end.
-- Note: D13: amount is a STRING of decimal base units, not a number. The domain type is RailAmount (BigInteger base units), so a numeric column would re-impose the 64-bit ceiling the widening removed -- an ordinary wei-denominated Ethereum amount does not fit in i64. Written via RailAmount.ToBaseUnitString(), read via RailAmount.TryParse.
-- Note: W3.3: an allocation's caller-supplied Purpose, and an issuance's ReserveAddress / ReservePartition / AllocationKind, live as keys INSIDE the FLEXIBLE parameters object -- deliberately, not as columns. SurrealBlockchainOperationStore.ToRow is the only writer of this table and it maps the whole parameters bag; it does not map arbitrary new columns, so a dedicated purpose column would be schema that nothing ever fills. Add one only together with the ToRow mapping.
-- Note: D16: value_currency_code / value_minor_units / value_rate / value_rate_as_of / value_rate_source are the fiat value stamped at settlement. They move as a group: all five NONE means the operation was never priced, which is deliberately distinguishable from value_rate = "0" (a real quote of zero). A partial stamp is not a stamp and reads back as unpriced. No rate source exists in this codebase yet -- IValueRateSource is the seam, and until something implements it every row is legitimately unstamped. Never backfill these from a later price: the point of storing the rate and its timestamp is that the figure stays auditable and re-derivable from what was actually known at settlement.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- OperationLog.StatusKind
DEFINE PARAM IF NOT EXISTS $operation_log_status VALUE ["Pending", "Unknown", "Failed", "Completed", "AwaitingSignature", "PendingConfirmation", "Minted", "Burned", "Exchanged", "Swapped", "Transferred", "Deployed", "Called", "NeedsOperator"];

DEFINE TABLE IF NOT EXISTS operation_log SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE operation_log TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS user_id ON TABLE operation_log TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS wallet_id ON TABLE operation_log TYPE option<record<wallet>>;
DEFINE FIELD IF NOT EXISTS operation_type ON TABLE operation_log TYPE string
    ASSERT $value != NONE AND $value != "";

-- Status (OperationStatus closed set)
DEFINE FIELD IF NOT EXISTS status ON TABLE operation_log TYPE string
    ASSERT $value INSIDE $operation_log_status
    DEFAULT "Pending";

-- Opaque parameters bag (Dictionary<string,string>)
DEFINE FIELD IF NOT EXISTS parameters ON TABLE operation_log TYPE option<object> FLEXIBLE;

-- IMintOperation fields
DEFINE FIELD IF NOT EXISTS token_uri ON TABLE operation_log TYPE option<string>;
DEFINE FIELD IF NOT EXISTS amount ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9]+$");
DEFINE FIELD IF NOT EXISTS asset_type ON TABLE operation_log TYPE option<string>;

-- IExchangeOperation fields
DEFINE FIELD IF NOT EXISTS source_holon_id ON TABLE operation_log TYPE option<record<holon>>;
DEFINE FIELD IF NOT EXISTS target_holon_id ON TABLE operation_log TYPE option<record<holon>>;
DEFINE FIELD IF NOT EXISTS exchange_rate ON TABLE operation_log TYPE option<string>;

-- ITransferOperation fields
DEFINE FIELD IF NOT EXISTS recipient_address ON TABLE operation_log TYPE option<string>;

-- G2 idempotency
DEFINE FIELD IF NOT EXISTS idempotency_key ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9a-f]{64}$");

-- Allocation receipt initiator context
DEFINE FIELD IF NOT EXISTS initiator_user_id ON TABLE operation_log TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS initiator_api_key_id ON TABLE operation_log TYPE option<record<api_key>>;

-- Error detail (populated on failure)
DEFINE FIELD IF NOT EXISTS error ON TABLE operation_log TYPE option<string>;

-- tenant-consent-delegation AC4: acting tenant + required signing scope
DEFINE FIELD IF NOT EXISTS acting_tenant_id ON TABLE operation_log TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS signing_scope ON TABLE operation_log TYPE option<string>;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_date ON TABLE operation_log TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS completed_date ON TABLE operation_log TYPE option<datetime>;

-- D16 fiat value at transaction time (all-NONE = never priced; see the table note)
DEFINE FIELD IF NOT EXISTS value_currency_code ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[A-Z]{3}$");
DEFINE FIELD IF NOT EXISTS value_minor_units ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9]+$");
DEFINE FIELD IF NOT EXISTS value_rate ON TABLE operation_log TYPE option<string>
    ASSERT $value = NONE OR string::matches($value, "^[0-9]+(\\.[0-9]+)?$");
DEFINE FIELD IF NOT EXISTS value_rate_as_of ON TABLE operation_log TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS value_rate_source ON TABLE operation_log TYPE option<string>;

-- Holon-asset link (economic-primitive-nodes)
DEFINE FIELD IF NOT EXISTS asset_id ON TABLE operation_log TYPE option<string>;
DEFINE FIELD IF NOT EXISTS tx_hash ON TABLE operation_log TYPE option<string>;
DEFINE FIELD IF NOT EXISTS holon_id ON TABLE operation_log TYPE option<record<holon>>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS operation_log_idempotency_key
    ON TABLE operation_log
    FIELDS idempotency_key
    UNIQUE;

DEFINE INDEX IF NOT EXISTS operation_log_user_created
    ON TABLE operation_log
    FIELDS user_id, created_date;

DEFINE INDEX IF NOT EXISTS operation_log_status
    ON TABLE operation_log
    FIELDS status;
