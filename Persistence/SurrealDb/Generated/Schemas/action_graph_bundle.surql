-- ============================================================
-- Table: action_graph_bundle
-- Aggregate: ActionGraphBundle (Models/ActionGraphBundle.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Generated code + deployment config can be large strings; SurrealDB has no per-field size limit but operators should monitor row size.
-- ============================================================

DEFINE TABLE IF NOT EXISTS action_graph_bundle SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE action_graph_bundle TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS name ON TABLE action_graph_bundle TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS description ON TABLE action_graph_bundle TYPE string;
DEFINE FIELD IF NOT EXISTS public_key ON TABLE action_graph_bundle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS private_key_hash ON TABLE action_graph_bundle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS user_id ON TABLE action_graph_bundle TYPE option<record<user>>;

-- BoundHolonIds (list of holon-id strings)
DEFINE FIELD IF NOT EXISTS bound_holon_ids ON TABLE action_graph_bundle TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS target_chain ON TABLE action_graph_bundle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS generated_code ON TABLE action_graph_bundle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS deployment_config ON TABLE action_graph_bundle TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE action_graph_bundle TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE action_graph_bundle TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE action_graph_bundle TYPE bool
    DEFAULT true;
DEFINE FIELD IF NOT EXISTS is_public ON TABLE action_graph_bundle TYPE bool
    DEFAULT false;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS action_graph_bundle_user_id
    ON TABLE action_graph_bundle
    FIELDS user_id;

DEFINE INDEX IF NOT EXISTS action_graph_bundle_target_chain
    ON TABLE action_graph_bundle
    FIELDS target_chain;

DEFINE INDEX IF NOT EXISTS action_graph_bundle_is_active
    ON TABLE action_graph_bundle
    FIELDS is_active;
