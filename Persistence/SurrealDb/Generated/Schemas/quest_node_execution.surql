-- ============================================================
-- Table: quest_node_execution
-- Aggregate: QuestNodeExecution (Models/Quest/QuestNodeExecution.cs)
-- Guardrail: G6 SCHEMAFULL; G2 exactly-once claim primitive (api-safety-hardening). One row per (run, node).
-- Note: Per-(run, node) execution row. Replaces the in-place mutation of QuestNode.State/Output/Error which prevented re-runs from preserving prior attempt's outputs.
-- Note: On a fork, the same execution row can be referenced by both parent and child run via the `executes` RELATE edge for nodes whose execution_order < forkPoint (copy-by-reference, no duplication). Scalar run_id is the originating run; cross-run reference happens through the RELATE edge defined separately in surrealdb-migration §6.2.
-- Note: The G2 claim primitive (TryClaimPendingAsync) is a conditional UPDATE that only succeeds when current state == 'Pending'. Empty-result case = lost race (already claimed by another worker). The (run_id, node_id) UNIQUE index is what makes the claim atomically safe.
-- Note: output stores the JSON-serialized OASISResult<T> from the handler call. error carries the failure message when state = Failed. Both are null in non-terminal states.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestNodeExecution.QuestNodeState
DEFINE PARAM IF NOT EXISTS $quest_node_execution_state VALUE ["Pending", "Running", "Succeeded", "Failed", "Skipped", "Cancelled"];

DEFINE TABLE IF NOT EXISTS quest_node_execution SCHEMAFULL;

-- Execution row identity (record id is the Guid('N') of QuestNodeExecution.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_node_execution TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning run (originating run; cross-run references via `executes` RELATE edge)
DEFINE FIELD IF NOT EXISTS run_id ON TABLE quest_node_execution TYPE record<quest_run>;

-- Quest definition node this execution corresponds to
DEFINE FIELD IF NOT EXISTS node_id ON TABLE quest_node_execution TYPE record<quest_node>;

-- QuestNodeState enum name
DEFINE FIELD IF NOT EXISTS state ON TABLE quest_node_execution TYPE string
    ASSERT $value INSIDE $quest_node_execution_state
    DEFAULT "Pending";

-- Serialized OASISResult<T> from the handler call (null until Succeeded)
DEFINE FIELD IF NOT EXISTS output ON TABLE quest_node_execution TYPE option<string>;

-- Failure message when state is Failed
DEFINE FIELD IF NOT EXISTS error ON TABLE quest_node_execution TYPE option<string>;

-- Wall-clock time at which the row entered Running
DEFINE FIELD IF NOT EXISTS started_at ON TABLE quest_node_execution TYPE datetime
    READONLY;

-- Wall-clock time at which the row reached a terminal state (null while non-terminal)
DEFINE FIELD IF NOT EXISTS ended_at ON TABLE quest_node_execution TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_node_execution_run_node
    ON TABLE quest_node_execution
    FIELDS run_id, node_id
    UNIQUE;

DEFINE INDEX IF NOT EXISTS quest_node_execution_by_run
    ON TABLE quest_node_execution
    FIELDS run_id;

DEFINE INDEX IF NOT EXISTS quest_node_execution_by_node
    ON TABLE quest_node_execution
    FIELDS node_id;

DEFINE INDEX IF NOT EXISTS quest_node_execution_by_state
    ON TABLE quest_node_execution
    FIELDS state;
