// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the holon_type_registry table
// (final-hardening-cutover Phase C / F5 — opt-in Holon AssetType registry).

#nullable enable

using System;
using System.Collections.Generic;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("holon_type_registry",
        Aggregate = "HolonType (Persistence/SurrealDb/Models/HolonType.cs)",
        Guardrail = "G6 SCHEMAFULL, unique index on asset_type (one registration per type)")]
    [SurrealNote("Opt-in registry that constrains a Holon.AssetType string (final-hardening-cutover F5). A row REGISTERS an asset-type name and, optionally, the metadata field names a holon of that type MUST carry. Opt-in = a Holon whose AssetType is NOT registered here is unconstrained (free string); only registered types are validated. Greenfield: no existing holon creation path breaks.")]
    [SurrealNote("Enforcement lives in HolonManager.ValidateAssetTypeAsync (Create + Update seams), NOT in the schema — the DB stores the spec, the manager applies it. Reads are open to any authenticated caller; registration/mutation is Operator-scoped (HolonTypeRegistryController), so this is a platform-governed vocabulary.")]
    [SurrealNote("asset_type is BOTH the record id and a UNIQUE-indexed column: a duplicate register is rejected per-statement (mirrors data_migration's applied-once ledger). required_metadata_fields is an array<string> of metadata keys asserted present+non-empty at holon create/update time.")]
    [Slice("identity")]
    [Index("holon_type_registry_asset_type_unique", Fields = new[] { "asset_type" }, Unique = true)]
    public partial class HolonType : ISurrealRecord
    {
        public const string SchemaNameConst = "holon_type_registry";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("The registered Holon.AssetType name this row governs (the opt-in key)")]
        [Required(NotEmpty = true)]
        public string AssetType { get; set; } = string.Empty;

        [Column(Order = 3)]
        [FieldGroup("Human-readable description of what this asset type represents")]
        public string Description { get; set; } = string.Empty;

        [Column(Order = 4)]
        [FieldGroup("Metadata keys a holon of this type MUST carry (present + non-empty). Empty/absent ⇒ no metadata constraint, type name alone is validated.")]
        public IReadOnlyList<string>? RequiredMetadataFields { get; set; }

        [Column(Order = 5)]
        [Default("true")]
        [FieldGroup("Inactive types are ignored by validation (opt-out without deleting history)")]
        public bool IsActive { get; set; }

        [Column(Order = 6)]
        [ReadOnly]
        [FieldGroup("Timestamps")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 7)]
        public DateTimeOffset? ModifiedAt { get; set; }
    }
}
