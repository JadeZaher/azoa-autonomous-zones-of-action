-- ============================================================
-- Table: holon
-- Aggregate: Holon (Models/Holon.cs)
-- Guardrail: G6 SCHEMAFULL
-- Note: ParentHolon / SubHolons are navigation properties from EF -- not persisted.
-- Note: Polyhierarchy graph remodel lives in surrealdb-migration task 10.
-- Note: metadata stored as option<object> (flexible key->value bag).
-- Note: peer_holon_ids stored as option<array> (list of "N"-format Guid strings).
-- ============================================================

DEFINE TABLE holon SCHEMAFULL;

DEFINE FIELD id ON TABLE holon TYPE string;
DEFINE FIELD name ON TABLE holon TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD description ON TABLE holon TYPE string;
DEFINE FIELD parent_holon_id ON TABLE holon TYPE option<string>;
DEFINE FIELD avatar_id ON TABLE holon TYPE option<string>;
DEFINE FIELD provider_name ON TABLE holon TYPE string
    ASSERT $value != NONE AND $value != "";
DEFINE FIELD chain_id ON TABLE holon TYPE option<string>;
DEFINE FIELD asset_type ON TABLE holon TYPE option<string>;
DEFINE FIELD token_id ON TABLE holon TYPE option<string>;

-- Metadata (flexible key->value bag)
DEFINE FIELD metadata ON TABLE holon TYPE option<object>;

-- PeerHolonIds (list of holon-id strings)
DEFINE FIELD peer_holon_ids ON TABLE holon TYPE option<array<string>>;

-- Embedding vector for HNSW semantic search (384-dimensional, MiniLM-style)
DEFINE FIELD embedding ON TABLE holon TYPE option<array<float, 384>>;

DEFINE FIELD created_date ON TABLE holon TYPE datetime;
DEFINE FIELD modified_date ON TABLE holon TYPE option<datetime>;
DEFINE FIELD is_active ON TABLE holon TYPE bool
    DEFAULT true;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX holon_avatar_id
    ON TABLE holon
    FIELDS avatar_id;

DEFINE INDEX holon_parent
    ON TABLE holon
    FIELDS parent_holon_id;

DEFINE INDEX holon_provider_chain
    ON TABLE holon
    FIELDS provider_name, chain_id;

DEFINE INDEX holon_asset_type
    ON TABLE holon
    FIELDS asset_type;
