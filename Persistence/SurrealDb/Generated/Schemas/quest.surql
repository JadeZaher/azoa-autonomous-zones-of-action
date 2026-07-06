-- ============================================================
-- Table: quest
-- Aggregate: Quest (Models/Quest/Quest.cs)
-- Guardrail: G6 SCHEMAFULL; definition-side workflow shape. Runtime state lives on quest_run + quest_node_execution per quest-temporal-fork-model ADR §1.
-- Note: Shape-only definition: name, owner, template ancestry, dapp-series binding, free-form metadata. completed_date was removed when quest-temporal-fork-model split runtime from definition. status was also removed then but is reintroduced by quest-dag-semantic-hardening FR-2 for the DEFINITION lifecycle (Draft/Active) -- distinct from the runtime QuestRun.Status. See Managers/AGENTS.md §publish-lifecycle.
-- Note: nodes/edges/dependencies live in their own tables (quest_node, quest_edge, quest_dependency) keyed by quest_id -- they are queried independently of the parent and mutated through Add/Remove endpoints. Embedding them would force the controller-side node/edge CRUD endpoints to read-modify-write the entire quest row.
-- Note: metadata is a free-form string->string map for caller-supplied tags (e.g. UI color, sort hint). Persisted as a SurrealDB object to keep arbitrary keys queryable.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- Quest.QuestStatusKind
DEFINE PARAM IF NOT EXISTS $quest_status VALUE ["Draft", "Active", "Completed", "Failed", "Archived"];

DEFINE TABLE IF NOT EXISTS quest SCHEMAFULL;

-- Core identity (record id is the Guid('N') of Quest.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owner avatar (Guid('N') hex)
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE quest TYPE record<avatar>;

-- Caller-supplied label
DEFINE FIELD IF NOT EXISTS name ON TABLE quest TYPE string
    ASSERT $value != NONE AND $value != "";

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE quest TYPE option<string>;

-- Source template id when instantiated from a QuestTemplate (null for hand-authored quests)
DEFINE FIELD IF NOT EXISTS template_id ON TABLE quest TYPE option<record<quest_template>>;

-- Owning DappSeries when this quest is part of a composed dApp (null for standalone quests)
DEFINE FIELD IF NOT EXISTS dapp_series_id ON TABLE quest TYPE option<record<dapp_series>>;

-- Free-form caller-supplied metadata (string->string map)
DEFINE FIELD IF NOT EXISTS metadata ON TABLE quest TYPE object FLEXIBLE;

-- Definition lifecycle: Draft (editable) or Active (locked for execution). See Managers/AGENTS.md §publish-lifecycle.
DEFINE FIELD IF NOT EXISTS status ON TABLE quest TYPE string
    ASSERT $value INSIDE $quest_status
    DEFAULT "Draft";

-- Optimistic-concurrency token for the definition lifecycle (F6 TOCTOU guard). Bumped on every CAS status transition. See Managers/AGENTS.md §publish-lifecycle.
DEFINE FIELD IF NOT EXISTS version ON TABLE quest TYPE int
    DEFAULT 0;

-- Definition birthdate -- STAYS on the definition, not a runtime artifact
DEFINE FIELD IF NOT EXISTS created_date ON TABLE quest TYPE datetime
    READONLY;

-- Embedding vector for HNSW semantic search (384-dimensional, MiniLM-style)
DEFINE FIELD IF NOT EXISTS embedding ON TABLE quest TYPE option<array<float, 384>>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_by_avatar
    ON TABLE quest
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS quest_by_template
    ON TABLE quest
    FIELDS template_id;

DEFINE INDEX IF NOT EXISTS quest_by_dapp_series
    ON TABLE quest
    FIELDS dapp_series_id;
