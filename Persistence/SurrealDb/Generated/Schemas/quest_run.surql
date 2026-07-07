-- ============================================================
-- Table: quest_run
-- Aggregate: QuestRun (Models/Quest/QuestRun.cs)
-- Guardrail: G6 SCHEMAFULL; runtime canonical lineage carrier. One row per execution attempt of a quest.
-- Note: Replaces the in-place mutation of Quest.Status that prevented re-runs from preserving prior attempt state -- see quest-temporal-fork-model ADR §1. Re-running a Succeeded quest creates a NEW root run (parent_run_id = null); forking a Running run creates a CHILD run with parent_run_id set.
-- Note: Lineage forms a TREE via parent_run_id, not a DAG. The forked_from RELATE edge (defined separately in surrealdb-migration §6.1) mirrors parent_run_id for native graph-traversal queries. Both must be kept in sync at write time.
-- Note: State machine invariants are enforced in QuestManager, not in the DB: Pending->Running (first claim), Running->{Succeeded|Failed|Forked|Cancelled} (terminal). fail_reason carries the supervisor-driven audit string; internal-error failures leave fail_reason null and write the error on the failed quest_node_execution row instead.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestRun.QuestRunStatusKind
DEFINE PARAM IF NOT EXISTS $quest_run_status VALUE ["Pending", "Running", "Succeeded", "Failed", "Forked", "Cancelled", "Suspended", "AwaitingSignal", "AwaitingTimer", "AwaitingReconciliation"];

DEFINE TABLE IF NOT EXISTS quest_run SCHEMAFULL;

-- Run identity (record id is the Guid('N') of QuestRun.Id) -- unique across all runs of all quests
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_run TYPE string
    ASSERT $value != NONE AND $value != "";

-- Quest definition this run executes
DEFINE FIELD IF NOT EXISTS quest_id ON TABLE quest_run TYPE record<quest>;

-- Avatar that initiated this run (denormalized for query convenience)
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE quest_run TYPE record<avatar>;

-- Tenant that drove this run via a tenant-driven child credential; null = user-driven. Carried on the durable row so the Tier-2 economic node handlers can stamp it on the produced BlockchainOperation and the custody signing seam's live consent check survives the async saga hop (tenant-consent-delegation AC4/AC4b). Mirrors OperationLog.acting_tenant_id.
DEFINE FIELD IF NOT EXISTS acting_tenant_id ON TABLE quest_run TYPE option<record<avatar>>;

-- QuestRunStatus enum name
DEFINE FIELD IF NOT EXISTS status ON TABLE quest_run TYPE string
    ASSERT $value INSIDE $quest_run_status
    DEFAULT "Pending";

-- Wall-clock time at which the run row was created
DEFINE FIELD IF NOT EXISTS started_at ON TABLE quest_run TYPE datetime
    READONLY;

-- Wall-clock time at which the run reached a terminal state (null while non-terminal)
DEFINE FIELD IF NOT EXISTS ended_at ON TABLE quest_run TYPE option<datetime>;

-- Parent run id if forked from another; null for root runs
DEFINE FIELD IF NOT EXISTS parent_run_id ON TABLE quest_run TYPE option<record<quest_run>>;

-- Node at which the fork occurred (set iff this is a child fork run)
DEFINE FIELD IF NOT EXISTS forked_at_node_id ON TABLE quest_run TYPE option<record<quest_node>>;

-- Free-form audit reason supplied when fork was triggered
DEFINE FIELD IF NOT EXISTS fork_reason ON TABLE quest_run TYPE option<string>;

-- Free-form audit reason when a supervisor explicitly marked the run failed (distinct from internal-error path)
DEFINE FIELD IF NOT EXISTS fail_reason ON TABLE quest_run TYPE option<string>;

-- Marketplace provenance: the origin quest definition this run was started from when the runner is not the quest owner (null for owner runs). See Managers/AGENTS.md §publish-lifecycle.
DEFINE FIELD IF NOT EXISTS source_quest_id ON TABLE quest_run TYPE option<record<quest>>;

-- Marketplace provenance: the origin quest's owner/creator avatar when the runner is not the owner (null for owner runs).
DEFINE FIELD IF NOT EXISTS origin_avatar_id ON TABLE quest_run TYPE option<record<avatar>>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_run_by_quest
    ON TABLE quest_run
    FIELDS quest_id;

DEFINE INDEX IF NOT EXISTS quest_run_by_avatar
    ON TABLE quest_run
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS quest_run_by_status
    ON TABLE quest_run
    FIELDS status;

DEFINE INDEX IF NOT EXISTS quest_run_by_parent
    ON TABLE quest_run
    FIELDS parent_run_id;

DEFINE INDEX IF NOT EXISTS quest_run_by_source_quest
    ON TABLE quest_run
    FIELDS source_quest_id;

DEFINE INDEX IF NOT EXISTS quest_run_by_origin_avatar
    ON TABLE quest_run
    FIELDS origin_avatar_id;
