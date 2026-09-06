-- ============================================================
-- Table: action_dependency
-- Aggregate: ActionDependency (Models/ActionDefinition/ActionDependency.cs)
-- Guardrail: G6 SCHEMAFULL; cross-action dependency edge. ActionDefinition activation refuses to enter Active until all Required dependencies are satisfied.
-- Note: Optional vs Required is a hint to the dependency-check endpoint -- Required dependencies block activation; Optional ones are reported in DependencyCheckResult for caller visibility but do not block.
-- Note: depends_on_node_id is set when the dependency is on a specific node's output (e.g. 'must have HolonCreate's holonId') rather than the entire action's completion. Null = depend on the whole action reaching Succeeded.
-- ============================================================

-- ── Enums ─────────────────────────────────────────────────

-- ActionDependency.ActionDependencyTypeKind
DEFINE PARAM IF NOT EXISTS $action_dependency_dependency_type VALUE ["Required", "Optional"];

DEFINE TABLE IF NOT EXISTS action_dependency SCHEMAFULL;

-- Core identity (record id is the Guid('N') of ActionDependency.Id)
DEFINE FIELD IF NOT EXISTS id ON TABLE action_dependency TYPE string
    ASSERT $value != NONE AND $value != "";

-- Dependent action (the one that won't run until the dependency is satisfied)
DEFINE FIELD IF NOT EXISTS action_id ON TABLE action_dependency TYPE record<action>;

-- Dependency target action
DEFINE FIELD IF NOT EXISTS depends_on_action_id ON TABLE action_dependency TYPE record<action>;

-- Optional: depend on a specific node output rather than full action completion
DEFINE FIELD IF NOT EXISTS depends_on_node_id ON TABLE action_dependency TYPE option<record<action_node>>;

-- ActionDependencyType enum name
DEFINE FIELD IF NOT EXISTS dependency_type ON TABLE action_dependency TYPE string
    ASSERT $value INSIDE $action_dependency_dependency_type
    DEFAULT "Required";

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_dependency_by_action
    ON TABLE action_dependency
    FIELDS action_id;

DEFINE INDEX IF NOT EXISTS action_dependency_by_target
    ON TABLE action_dependency
    FIELDS depends_on_action_id;
