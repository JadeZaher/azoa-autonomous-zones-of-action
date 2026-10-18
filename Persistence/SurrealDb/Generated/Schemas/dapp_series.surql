-- ============================================================
-- Table: dapp_series
-- Aggregate: DappSeries (dapp-composition track -- new entity, no hand-written model)
-- Guardrail: G6 SCHEMAFULL; greenfield entity introduced by the dapp-composition track. Owns the composed-action-series shape.
-- Note: A DappSeries is a linked series of action DAGs that compose into a deployable dApp contract via action graph bundle generation. Status transitions: Draft -> Building -> Ready -> Deployed -> Archived. The action entries are stored separately in dapp_series_action (table 220) keyed by dapp_series_id; they are queried + reordered independently of the parent.
-- Note: shared_config is a string->string map for cross-action deployment settings (e.g. chain, provider RPC URL). It is overlaid with each DappSeriesAction's input mappings to produce the per-action runtime config at execution time.
-- Note: manifest stores the composed DappManifest as JSON-on-row (string-encoded). DappManifest is a transient artifact produced by ComposeAsync -- it is NOT a separate aggregate root because it is always read whole alongside its parent series. action_graph_bundle_id is populated by GenerateAsync after the ActionGraphBundle record is created via IActionGraphBundleManager.CreateOrUpdateAsync.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- DappSeries.StatusKind
DEFINE PARAM IF NOT EXISTS $dapp_series_status VALUE ["Draft", "Building", "Ready", "Deploying", "DeploymentFailed", "Deployed", "Archived"];

DEFINE TABLE IF NOT EXISTS dapp_series SCHEMAFULL;

-- Core identity (record id is the Guid('N') of DappSeries.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE dapp_series TYPE string
    ASSERT $value != NONE AND $value != "";

-- Caller-supplied dApp name
DEFINE FIELD IF NOT EXISTS name ON TABLE dapp_series TYPE string
    ASSERT $value != NONE AND $value != "";

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE dapp_series TYPE option<string>;

-- Owner user (Guid('N') hex)
DEFINE FIELD IF NOT EXISTS user_id ON TABLE dapp_series TYPE record<user>;

-- DappSeriesStatus enum name
DEFINE FIELD IF NOT EXISTS status ON TABLE dapp_series TYPE string
    ASSERT $value INSIDE $dapp_series_status
    DEFAULT "Draft";

-- Shared deployment config across all actions in the series (string->string map)
DEFINE FIELD IF NOT EXISTS shared_config ON TABLE dapp_series TYPE object;

-- Linked ActionGraphBundle.Id, populated by GenerateAsync (null until generation)
DEFINE FIELD IF NOT EXISTS action_graph_bundle_id ON TABLE dapp_series TYPE option<record<action_graph_bundle>>;
DEFINE FIELD IF NOT EXISTS deployment_operation_id ON TABLE dapp_series TYPE option<string>;

-- Deployment target chain (e.g. algorand-mainnet) -- nullable until set
DEFINE FIELD IF NOT EXISTS target_chain ON TABLE dapp_series TYPE option<string>;

-- Composed DappManifest as JSON-on-row (null until ComposeAsync runs)
DEFINE FIELD IF NOT EXISTS manifest ON TABLE dapp_series TYPE option<string>;

-- Creation timestamp
DEFINE FIELD IF NOT EXISTS created_date ON TABLE dapp_series TYPE datetime
    READONLY;

-- Deployment timestamp (null until Deployed)
DEFINE FIELD IF NOT EXISTS deployed_date ON TABLE dapp_series TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS dapp_series_by_user
    ON TABLE dapp_series
    FIELDS user_id;

DEFINE INDEX IF NOT EXISTS dapp_series_by_status
    ON TABLE dapp_series
    FIELDS status;

DEFINE INDEX IF NOT EXISTS dapp_series_by_action_graph_bundle
    ON TABLE dapp_series
    FIELDS action_graph_bundle_id;
