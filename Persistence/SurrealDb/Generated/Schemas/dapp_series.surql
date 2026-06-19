-- ============================================================
-- Table: dapp_series
-- Aggregate: DappSeries (dapp-composition track -- new entity, no hand-written model)
-- Guardrail: G6 SCHEMAFULL; greenfield entity introduced by the dapp-composition track. Owns the composed-quest-series shape.
-- Note: A DappSeries is a linked series of quest DAGs that compose into a deployable dApp contract via STAR generation. Status transitions: Draft -> Building -> Ready -> Deployed -> Archived. The quest entries are stored separately in dapp_series_quest (table 220) keyed by dapp_series_id; they are queried + reordered independently of the parent.
-- Note: shared_config is a string->string map for cross-quest deployment settings (e.g. chain, provider RPC URL). It is overlaid with each DappSeriesQuest's input mappings to produce the per-quest runtime config at execution time.
-- Note: manifest stores the composed DappManifest as JSON-on-row (string-encoded). DappManifest is a transient artifact produced by ComposeAsync -- it is NOT a separate aggregate root because it is always read whole alongside its parent series. star_odk_id is populated by GenerateAsync after the STARODK record is created via ISTARManager.CreateOrUpdateAsync.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- DappSeries.StatusKind
DEFINE PARAM IF NOT EXISTS $dapp_series_status VALUE ["Draft", "Building", "Ready", "Deployed", "Archived"];

DEFINE TABLE IF NOT EXISTS dapp_series SCHEMAFULL;

-- Core identity (record id is the Guid('N') of DappSeries.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE dapp_series TYPE string
    ASSERT $value != NONE AND $value != "";

-- Caller-supplied dApp name
DEFINE FIELD IF NOT EXISTS name ON TABLE dapp_series TYPE string
    ASSERT $value != NONE AND $value != "";

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE dapp_series TYPE option<string>;

-- Owner avatar (Guid('N') hex)
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE dapp_series TYPE record<avatar>;

-- DappSeriesStatus enum name
DEFINE FIELD IF NOT EXISTS status ON TABLE dapp_series TYPE string
    ASSERT $value INSIDE $dapp_series_status
    DEFAULT "Draft";

-- Shared deployment config across all quests in the series (string->string map)
DEFINE FIELD IF NOT EXISTS shared_config ON TABLE dapp_series TYPE object;

-- Linked STARODK.Id, populated by GenerateAsync (null until generation)
DEFINE FIELD IF NOT EXISTS star_odk_id ON TABLE dapp_series TYPE option<record<star_odk>>;

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

DEFINE INDEX IF NOT EXISTS dapp_series_by_avatar
    ON TABLE dapp_series
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS dapp_series_by_status
    ON TABLE dapp_series
    FIELDS status;

DEFINE INDEX IF NOT EXISTS dapp_series_by_star_odk
    ON TABLE dapp_series
    FIELDS star_odk_id;
