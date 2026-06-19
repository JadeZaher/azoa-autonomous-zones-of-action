-- ============================================================
-- Table: star_odk
-- Aggregate: STARODK (Models/STARODK.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: Generated code + deployment config can be large strings; SurrealDB has no per-field size limit but operators should monitor row size.
-- ============================================================

DEFINE TABLE IF NOT EXISTS star_odk SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE star_odk TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS name ON TABLE star_odk TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS description ON TABLE star_odk TYPE string;
DEFINE FIELD IF NOT EXISTS public_key ON TABLE star_odk TYPE option<string>;
DEFINE FIELD IF NOT EXISTS private_key_hash ON TABLE star_odk TYPE option<string>;
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE star_odk TYPE option<record<avatar>>;

-- BoundHolonIds (list of holon-id strings)
DEFINE FIELD IF NOT EXISTS bound_holon_ids ON TABLE star_odk TYPE option<array<string>>;
DEFINE FIELD IF NOT EXISTS target_chain ON TABLE star_odk TYPE option<string>;
DEFINE FIELD IF NOT EXISTS generated_code ON TABLE star_odk TYPE option<string>;
DEFINE FIELD IF NOT EXISTS deployment_config ON TABLE star_odk TYPE option<string>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE star_odk TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE star_odk TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE star_odk TYPE bool
    DEFAULT true;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS star_avatar_id
    ON TABLE star_odk
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS star_target_chain
    ON TABLE star_odk
    FIELDS target_chain;

DEFINE INDEX IF NOT EXISTS star_is_active
    ON TABLE star_odk
    FIELDS is_active;
