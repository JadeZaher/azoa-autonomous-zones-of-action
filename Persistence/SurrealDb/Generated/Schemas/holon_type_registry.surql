-- ============================================================
-- Table: holon_type_registry
-- Aggregate: HolonType (Persistence/SurrealDb/Models/HolonType.cs)
-- Guardrail: G6 SCHEMAFULL, unique index on asset_type (one registration per type)
-- Note: Opt-in registry that constrains a Holon.AssetType string (final-hardening-cutover F5). A row REGISTERS an asset-type name and, optionally, the metadata field names a holon of that type MUST carry. Opt-in = a Holon whose AssetType is NOT registered here is unconstrained (free string); only registered types are validated. Greenfield: no existing holon creation path breaks.
-- Note: Enforcement lives in HolonManager.ValidateAssetTypeAsync (Create + Update seams), NOT in the schema — the DB stores the spec, the manager applies it. Reads are open to any authenticated caller; registration/mutation is Operator-scoped (HolonTypeRegistryController), so this is a platform-governed vocabulary.
-- Note: asset_type is BOTH the record id and a UNIQUE-indexed column: a duplicate register is rejected per-statement (mirrors data_migration's applied-once ledger). required_metadata_fields is an array<string> of metadata keys asserted present+non-empty at holon create/update time.
-- ============================================================

DEFINE TABLE IF NOT EXISTS holon_type_registry SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE holon_type_registry TYPE string
    ASSERT $value != NONE AND $value != "";

-- The registered Holon.AssetType name this row governs (the opt-in key)
DEFINE FIELD IF NOT EXISTS asset_type ON TABLE holon_type_registry TYPE string
    ASSERT $value != NONE AND $value != "";

-- Human-readable description of what this asset type represents
DEFINE FIELD IF NOT EXISTS description ON TABLE holon_type_registry TYPE string;

-- Metadata keys a holon of this type MUST carry (present + non-empty). Empty/absent ⇒ no metadata constraint, type name alone is validated.
DEFINE FIELD IF NOT EXISTS required_metadata_fields ON TABLE holon_type_registry TYPE option<array<string>>;

-- Inactive types are ignored by validation (opt-out without deleting history)
DEFINE FIELD IF NOT EXISTS is_active ON TABLE holon_type_registry TYPE bool
    DEFAULT true;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE holon_type_registry TYPE datetime
    READONLY;
DEFINE FIELD IF NOT EXISTS modified_at ON TABLE holon_type_registry TYPE option<datetime>;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS holon_type_registry_asset_type_unique
    ON TABLE holon_type_registry
    FIELDS asset_type
    UNIQUE;
