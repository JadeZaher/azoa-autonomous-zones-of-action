-- ============================================================
-- Table: action_run
-- Aggregate: ActionRun (Models/ActionDefinition/ActionRun.cs)
-- Guardrail: G6 SCHEMAFULL; runtime canonical lineage carrier. One row per execution attempt of an action.
-- Note: Replaces the in-place mutation of ActionDefinition.Status that prevented re-runs from preserving prior attempt state -- see action-temporal-fork-model ADR §1. Re-running a Succeeded action creates a NEW root run (parent_run_id = null); forking a Running run creates a CHILD run with parent_run_id set.
-- Note: Lineage forms a TREE via parent_run_id, not a DAG. The forked_from RELATE edge (defined separately in surrealdb-migration §6.1) mirrors parent_run_id for native graph-traversal queries. Both must be kept in sync at write time.
-- Note: State machine invariants are enforced in ActionManager, not in the DB: Pending->Running (first claim), Running->{Succeeded|Failed|Forked|Cancelled} (terminal). fail_reason carries the supervisor-driven audit string; internal-error failures leave fail_reason null and write the error on the failed action_node_execution row instead.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ActionRun.ActionRunStatusKind
DEFINE PARAM IF NOT EXISTS $action_run_status VALUE ["Pending", "Running", "Succeeded", "Failed", "Forked", "Cancelled", "Suspended", "AwaitingSignal", "AwaitingTimer", "AwaitingReconciliation"];

DEFINE TABLE IF NOT EXISTS action_run SCHEMAFULL;

-- Run identity (record id is the Guid('N') of ActionRun.Id) -- unique across all runs of all actions
DEFINE FIELD IF NOT EXISTS id ON TABLE action_run TYPE string
    ASSERT $value != NONE AND $value != "";

-- ActionDefinition definition this run executes
DEFINE FIELD IF NOT EXISTS action_id ON TABLE action_run TYPE record<action>;

-- User that initiated this run (denormalized for query convenience)
DEFINE FIELD IF NOT EXISTS user_id ON TABLE action_run TYPE record<user>;

-- Tenant that drove this run via a tenant-driven child credential; null = user-driven. Carried on the durable row so the Tier-2 economic node handlers can stamp it on the produced BlockchainOperation and the custody signing seam's live consent check survives the async saga hop (tenant-consent-delegation AC4/AC4b). Mirrors OperationLog.acting_tenant_id.
DEFINE FIELD IF NOT EXISTS acting_tenant_id ON TABLE action_run TYPE option<record<user>>;

-- ActionRunStatus enum name
DEFINE FIELD IF NOT EXISTS status ON TABLE action_run TYPE string
    ASSERT $value INSIDE $action_run_status
    DEFAULT "Pending";

-- Wall-clock time at which the run row was created
DEFINE FIELD IF NOT EXISTS started_at ON TABLE action_run TYPE datetime
    READONLY;

-- Wall-clock time at which the run reached a terminal state (null while non-terminal)
DEFINE FIELD IF NOT EXISTS ended_at ON TABLE action_run TYPE option<datetime>;

-- Parent run id if forked from another; null for root runs
DEFINE FIELD IF NOT EXISTS parent_run_id ON TABLE action_run TYPE option<record<action_run>>;

-- Node at which the fork occurred (set iff this is a child fork run)
DEFINE FIELD IF NOT EXISTS forked_at_node_id ON TABLE action_run TYPE option<record<action_node>>;

-- Free-form audit reason supplied when fork was triggered
DEFINE FIELD IF NOT EXISTS fork_reason ON TABLE action_run TYPE option<string>;

-- Free-form audit reason when a supervisor explicitly marked the run failed (distinct from internal-error path)
DEFINE FIELD IF NOT EXISTS fail_reason ON TABLE action_run TYPE option<string>;

-- Marketplace provenance: the origin action definition this run was started from when the runner is not the action owner (null for owner runs). See Managers/AGENTS.md §publish-lifecycle.
DEFINE FIELD IF NOT EXISTS source_action_id ON TABLE action_run TYPE option<record<action>>;

-- Marketplace provenance: the origin action's owner/creator user when the runner is not the owner (null for owner runs).
DEFINE FIELD IF NOT EXISTS origin_user_id ON TABLE action_run TYPE option<record<user>>;

-- The action's published_version_hash at the instant this run STARTED -- binds the run to the exact graph revision the runner saw/consented to. A run whose stamp no longer matches the live action is detectable evidence of an unpublish->edit->republish bait-and-switch. See Managers/AGENTS.md §published-version-hash.
DEFINE FIELD IF NOT EXISTS published_version_hash ON TABLE action_run TYPE option<string>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_run_by_action
    ON TABLE action_run
    FIELDS action_id;

DEFINE INDEX IF NOT EXISTS action_run_by_user
    ON TABLE action_run
    FIELDS user_id;

DEFINE INDEX IF NOT EXISTS action_run_by_status
    ON TABLE action_run
    FIELDS status;

DEFINE INDEX IF NOT EXISTS action_run_by_parent
    ON TABLE action_run
    FIELDS parent_run_id;

DEFINE INDEX IF NOT EXISTS action_run_by_source_action
    ON TABLE action_run
    FIELDS source_action_id;

DEFINE INDEX IF NOT EXISTS action_run_by_origin_user
    ON TABLE action_run
    FIELDS origin_user_id;
