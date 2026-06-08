-- ============================================================
-- Table: quest_dependency
-- Aggregate: QuestDependency (Models/Quest/QuestDependency.cs)
-- Guardrail: G6 SCHEMAFULL; cross-quest dependency edge. Quest activation refuses to enter Active until all Required dependencies are satisfied.
-- Note: Optional vs Required is a hint to the dependency-check endpoint -- Required dependencies block activation; Optional ones are reported in DependencyCheckResult for caller visibility but do not block.
-- Note: depends_on_node_id is set when the dependency is on a specific node's output (e.g. 'must have HolonCreate's holonId') rather than the entire quest's completion. Null = depend on the whole quest reaching Succeeded.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- QuestDependency.QuestDependencyTypeKind
DEFINE PARAM IF NOT EXISTS $quest_dependency_dependency_type VALUE ["Required", "Optional"];

DEFINE TABLE IF NOT EXISTS quest_dependency SCHEMAFULL;

-- Core identity (record id is the Guid('N') of QuestDependency.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE quest_dependency TYPE string
    ASSERT $value != NONE AND $value != "";

-- Dependent quest (the one that won't run until the dependency is satisfied)
DEFINE FIELD IF NOT EXISTS quest_id ON TABLE quest_dependency TYPE record<quest>;

-- Dependency target quest
DEFINE FIELD IF NOT EXISTS depends_on_quest_id ON TABLE quest_dependency TYPE record<quest>;

-- Optional: depend on a specific node output rather than full quest completion
DEFINE FIELD IF NOT EXISTS depends_on_node_id ON TABLE quest_dependency TYPE option<record<quest_node>>;

-- QuestDependencyType enum name
DEFINE FIELD IF NOT EXISTS dependency_type ON TABLE quest_dependency TYPE string
    ASSERT $value INSIDE $quest_dependency_dependency_type
    DEFAULT "Required";

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS quest_dependency_by_quest
    ON TABLE quest_dependency
    FIELDS quest_id;

DEFINE INDEX IF NOT EXISTS quest_dependency_by_target
    ON TABLE quest_dependency
    FIELDS depends_on_quest_id;
