-- ============================================================
-- Table: holon
-- Aggregate: Holon (Models/Holon.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: ParentHolon / SubHolons are navigation properties from EF -- not persisted. Polyhierarchy graph remodel lives in surrealdb-migration task 10.
-- ============================================================

DEFINE TABLE IF NOT EXISTS holon SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE holon TYPE string;
DEFINE FIELD IF NOT EXISTS name ON TABLE holon TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS description ON TABLE holon TYPE string;
DEFINE FIELD IF NOT EXISTS parent_holon_id ON TABLE holon TYPE option<record<holon>>;
DEFINE FIELD IF NOT EXISTS avatar_id ON TABLE holon TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS provider_name ON TABLE holon TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD IF NOT EXISTS chain_id ON TABLE holon TYPE option<string>;
DEFINE FIELD IF NOT EXISTS asset_type ON TABLE holon TYPE option<string>;
DEFINE FIELD IF NOT EXISTS token_id ON TABLE holon TYPE option<string>;

-- Metadata (flexible key->value bag)
DEFINE FIELD IF NOT EXISTS metadata ON TABLE holon TYPE option<object> FLEXIBLE;

-- PeerHolonIds (list of holon-id strings)
DEFINE FIELD IF NOT EXISTS peer_holon_ids ON TABLE holon TYPE option<array<string>>;

-- Embedding vector for HNSW semantic search (384-dimensional, MiniLM-style)
DEFINE FIELD IF NOT EXISTS embedding ON TABLE holon TYPE option<array<float, 384>>;
DEFINE FIELD IF NOT EXISTS created_date ON TABLE holon TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_date ON TABLE holon TYPE option<datetime>;
DEFINE FIELD IF NOT EXISTS is_active ON TABLE holon TYPE bool
    DEFAULT true;
DEFINE FIELD IF NOT EXISTS source_holon_id ON TABLE holon TYPE option<record<holon>>;
DEFINE FIELD IF NOT EXISTS origin_avatar_id ON TABLE holon TYPE option<record<avatar>>;
DEFINE FIELD IF NOT EXISTS is_public ON TABLE holon TYPE bool
    DEFAULT false;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS holon_avatar_id
    ON TABLE holon
    FIELDS avatar_id;

DEFINE INDEX IF NOT EXISTS holon_parent
    ON TABLE holon
    FIELDS parent_holon_id;

DEFINE INDEX IF NOT EXISTS holon_provider_chain
    ON TABLE holon
    FIELDS provider_name, chain_id;

DEFINE INDEX IF NOT EXISTS holon_asset_type
    ON TABLE holon
    FIELDS asset_type;
