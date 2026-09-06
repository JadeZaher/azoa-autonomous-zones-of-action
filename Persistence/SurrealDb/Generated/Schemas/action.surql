-- ============================================================
-- Table: action
-- Aggregate: ActionDefinition (Models/ActionDefinition/ActionDefinition.cs)
-- Guardrail: G6 SCHEMAFULL; definition-side workflow shape. Runtime state lives on action_run + action_node_execution per action-temporal-fork-model ADR §1.
-- Note: Shape-only definition: name, owner, template ancestry, dapp-series binding, free-form metadata. completed_date was removed when action-temporal-fork-model split runtime from definition. status was also removed then but is reintroduced by action-dag-semantic-hardening FR-2 for the DEFINITION lifecycle (Draft/Active) -- distinct from the runtime ActionRun.Status. See Managers/AGENTS.md §publish-lifecycle.
-- Note: nodes/edges/dependencies live in their own tables (action_node, action_edge, action_dependency) keyed by action_id -- they are queried independently of the parent and mutated through Add/Remove endpoints. Embedding them would force the controller-side node/edge CRUD endpoints to read-modify-write the entire action row.
-- Note: metadata is a free-form string->string map for caller-supplied tags (e.g. UI color, sort hint). Persisted as a SurrealDB object to keep arbitrary keys queryable.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ActionDefinition.ActionStatusKind
DEFINE PARAM IF NOT EXISTS $action_status VALUE ["Draft", "Active", "Completed", "Failed", "Archived"];
-- ActionDefinition.ActionRunAccessKind
DEFINE PARAM IF NOT EXISTS $action_run_access VALUE ["Open", "InviteOnly"];

DEFINE TABLE IF NOT EXISTS action SCHEMAFULL;

-- Core identity (record id is the Guid('N') of ActionDefinition.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE action TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owner user (Guid('N') hex)
DEFINE FIELD IF NOT EXISTS user_id ON TABLE action TYPE record<user>;

-- Caller-supplied label
DEFINE FIELD IF NOT EXISTS name ON TABLE action TYPE string
    ASSERT $value != NONE AND $value != "";

-- Optional description
DEFINE FIELD IF NOT EXISTS description ON TABLE action TYPE option<string>;

-- Source template id when instantiated from a ActionTemplate (null for hand-authored actions)
DEFINE FIELD IF NOT EXISTS template_id ON TABLE action TYPE option<record<action_template>>;

-- Owning DappSeries when this action is part of a composed dApp (null for standalone actions)
DEFINE FIELD IF NOT EXISTS dapp_series_id ON TABLE action TYPE option<record<dapp_series>>;

-- Free-form caller-supplied metadata (string->string map)
DEFINE FIELD IF NOT EXISTS metadata ON TABLE action TYPE object FLEXIBLE;

-- Definition lifecycle: Draft (editable) or Active (locked for execution). See Managers/AGENTS.md §publish-lifecycle.
DEFINE FIELD IF NOT EXISTS status ON TABLE action TYPE string
    ASSERT $value INSIDE $action_status
    DEFAULT "Draft";

-- Optimistic-concurrency token for the definition lifecycle (F6 TOCTOU guard). Bumped on every CAS status transition. See Managers/AGENTS.md §publish-lifecycle.
DEFINE FIELD IF NOT EXISTS version ON TABLE action TYPE int
    DEFAULT 0;

-- Marketplace visibility: false = private (owner-only), true = published so a non-owner can start it under their own context. See Managers/AGENTS.md §publish-lifecycle.
DEFINE FIELD IF NOT EXISTS is_public ON TABLE action TYPE bool
    DEFAULT false;

-- Denormalised origin/creator user when this action is a marketplace copy (null for first-party actions). Provenance back to the action this was cloned/instantiated from.
DEFINE FIELD IF NOT EXISTS origin_user_id ON TABLE action TYPE option<record<user>>;

-- Content hash of the node/edge graph as of the last publish (bait-and-switch guard). Null while never published; recomputed on every PublishAsync. Stamped onto each action_run at start so a run is bound to the exact graph revision the runner saw. See Managers/AGENTS.md §published-version-hash.
DEFINE FIELD IF NOT EXISTS published_version_hash ON TABLE action TYPE option<string>;

-- Run-authorization dimension, orthogonal to is_public (discoverability). Open = anyone who can view may run; InviteOnly = owner + invited_user_ids only. See Persistence/SurrealDb/Models/AGENTS.md §action-access-request.
DEFINE FIELD IF NOT EXISTS run_access ON TABLE action TYPE string
    ASSERT $value INSIDE $action_run_access
    DEFAULT "Open";

-- Approved invite set for InviteOnly actions (user-id hex); owner is always implicitly invited.
DEFINE FIELD IF NOT EXISTS invited_user_ids ON TABLE action TYPE option<array<string>>;

-- Definition birthdate -- STAYS on the definition, not a runtime artifact
DEFINE FIELD IF NOT EXISTS created_date ON TABLE action TYPE datetime
    READONLY;

-- Embedding vector for HNSW semantic search (384-dimensional, MiniLM-style)
DEFINE FIELD IF NOT EXISTS embedding ON TABLE action TYPE option<array<float, 384>>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_by_user
    ON TABLE action
    FIELDS user_id;

DEFINE INDEX IF NOT EXISTS action_by_template
    ON TABLE action
    FIELDS template_id;

DEFINE INDEX IF NOT EXISTS action_by_dapp_series
    ON TABLE action
    FIELDS dapp_series_id;
