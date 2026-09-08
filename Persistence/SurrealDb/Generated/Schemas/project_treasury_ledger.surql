-- ============================================================
-- Table: project_treasury_ledger
-- Aggregate: ProjectTreasury (Services/Reserve/ProjectTreasuryService.cs)
-- Guardrail: G6 SCHEMAFULL; one custodial treasury account per (chain, network, tenant, project)
-- Note: The node-side account of ONE project's own custodial treasury: the account funding actually moves TO. It is NOT the node's fee destination -- that is node_treasury_destination, a different concept -- and it is NOT the platform reserve, which is the operator's single pooled account.
-- Note: treasury_address is the address DERIVED from the chain's platform key for this (chain, tenant, project) triple, frozen at first resolve. Every later resolve re-derives and compares: a platform-key rotation moves the derived address, and a mismatch FAILS CLOSED instead of quietly pointing the project at an account holding none of its value.
-- Note: assets is a flexible object keyed by asset id, each value carrying credited/spent/held as exact decimal STRINGS so an amount wider than i64 is never truncated. Available is credited - spent - held; the node refuses a spend or a hold that would take it negative.
-- Note: The counters are a record of COMMITMENT, not of chain balance. Chain truth remains the balance (data-engine guardrail); what chain state cannot express is how much of the balance is already promised to a payout that has not settled.
-- Note: recent_operation_keys is a bounded FIFO replay window of applied movement operation keys. A retried movement whose key is still in the window returns the counters unchanged rather than double-counting; a key that has aged out would apply again, which is why callers anchor their own idempotency as well.
-- Note: version is the compare-and-swap anchor. Every movement reads the row, applies the arithmetic in process, and writes back only while version is unchanged, so two concurrent spends cannot both consume the same availability.
-- Note: project_treasury_ledger_address is UNIQUE on purpose. Two projects sharing one derived address would mean the derivation lost part of its input, and this index turns that class of bug into a write failure instead of a silent pooling of two projects' money.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

DEFINE PARAM IF NOT EXISTS $project_treasury_ledger_network VALUE ["Devnet", "Testnet", "Mainnet"];
DEFINE PARAM IF NOT EXISTS $project_treasury_ledger_status VALUE ["Open", "Closed"];

DEFINE TABLE IF NOT EXISTS project_treasury_ledger SCHEMAFULL;

-- Core identity (record id is the SHA-256 of chain|network|tenant|project)
DEFINE FIELD IF NOT EXISTS id ON TABLE project_treasury_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- The project this treasury belongs to, and where it lives
DEFINE FIELD IF NOT EXISTS chain ON TABLE project_treasury_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS network ON TABLE project_treasury_ledger TYPE string
    ASSERT $value INSIDE $project_treasury_ledger_network;
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE project_treasury_ledger TYPE record<user>;
DEFINE FIELD IF NOT EXISTS project_reference ON TABLE project_treasury_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- The derived custodial account (frozen so a platform-key rotation is visible)
DEFINE FIELD IF NOT EXISTS treasury_address ON TABLE project_treasury_ledger TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS derivation_version ON TABLE project_treasury_ledger TYPE string
    ASSERT $value != NONE AND $value != "";

-- Lifecycle
DEFINE FIELD IF NOT EXISTS status ON TABLE project_treasury_ledger TYPE string
    ASSERT $value INSIDE $project_treasury_ledger_status;
DEFINE FIELD IF NOT EXISTS closed_at ON TABLE project_treasury_ledger TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS closed_by_user_id ON TABLE project_treasury_ledger TYPE option<record<user>>;
DEFINE FIELD IF NOT EXISTS close_reason ON TABLE project_treasury_ledger TYPE option<string>;

-- Per-asset counters -- credited, spent, held
DEFINE FIELD IF NOT EXISTS assets ON TABLE project_treasury_ledger TYPE option<object> FLEXIBLE;

-- Replay window for movement operation keys
DEFINE FIELD IF NOT EXISTS recent_operation_keys ON TABLE project_treasury_ledger TYPE option<array<string>>;

-- Optimistic concurrency + timestamps
DEFINE FIELD IF NOT EXISTS version ON TABLE project_treasury_ledger TYPE int
    DEFAULT 0;
DEFINE FIELD IF NOT EXISTS created_at ON TABLE project_treasury_ledger TYPE datetime
    DEFAULT time::now()
    READONLY;
DEFINE FIELD IF NOT EXISTS updated_at ON TABLE project_treasury_ledger TYPE datetime;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS project_treasury_ledger_project
    ON TABLE project_treasury_ledger
    FIELDS chain, network, tenant_id, project_reference
    UNIQUE;

DEFINE INDEX IF NOT EXISTS project_treasury_ledger_address
    ON TABLE project_treasury_ledger
    FIELDS chain, network, treasury_address
    UNIQUE;

DEFINE INDEX IF NOT EXISTS project_treasury_ledger_tenant
    ON TABLE project_treasury_ledger
    FIELDS tenant_id;
