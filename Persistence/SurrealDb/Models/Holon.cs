// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the holon table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("holon",
        Aggregate = "Holon (Models/Holon.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("ParentHolon / SubHolons are navigation properties from EF -- not persisted. Polyhierarchy graph remodel lives in surrealdb-migration task 10.")]
    [Slice("identity")]
    [Index("holon_avatar_id", Fields = new[] { "avatar_id" })]
    [Index("holon_parent", Fields = new[] { "parent_holon_id" })]
    [Index("holon_provider_chain", Fields = new[] { "provider_name", "chain_id" })]
    [Index("holon_asset_type", Fields = new[] { "asset_type" })]
    [ExtraSurrealField("embedding", "option<array<float, 384>>", Order = 12,
        FieldGroup = "Embedding vector for HNSW semantic search (384-dimensional, MiniLM-style)")]
    public partial class Holon : ISurrealRecord
    {
        public const string SchemaNameConst = "holon";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 3)]
        public string Description { get; set; } = string.Empty;

        [Column(Order = 4)]
        [References(typeof(Holon), Optional = true)]
        public string? ParentHolonId { get; set; }

        [Column(Order = 5)]
        [References(typeof(Avatar), Optional = true)]
        public string? AvatarId { get; set; }

        [Column(Order = 6)]
        [Required(NotEmpty = true)]
        public string ProviderName { get; set; } = string.Empty;

        [Column(Order = 7)]
        public string? ChainId { get; set; }

        [Column(Order = 8)]
        public string? AssetType { get; set; }

        [Column(Order = 9)]
        public string? TokenId { get; set; }

        [Column(Order = 10, Flexible = true)]
        [FieldGroup("Metadata (flexible key->value bag)")]
        public JsonElement? Metadata { get; set; }

        [Column(Order = 11)]
        [FieldGroup("PeerHolonIds (list of holon-id strings)")]
        public IReadOnlyList<string>? PeerHolonIds { get; set; }

        // Order = 12 is the [ExtraSurrealField("embedding", ...)] declared at class level.

        [Column(Order = 13)]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 14)]
        public DateTimeOffset? ModifiedDate { get; set; }

        [Column(Order = 15)]
        [Default("true")]
        public bool IsActive { get; set; }

        [Column(Order = 16)]
        [References(typeof(Holon), Optional = true)]
        public string? SourceHolonId { get; set; }

        [Column(Order = 17)]
        [References(typeof(Avatar), Optional = true)]
        public string? OriginAvatarId { get; set; }

        [Column(Order = 18)]
        [Default("false")]
        public bool IsPublic { get; set; }
    }
}
