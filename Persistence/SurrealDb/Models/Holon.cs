// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the holon table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
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

        [Id, Column(Order = 1, Type = "string")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [Column(Order = 4)]
        [References(typeof(Holon), Optional = true)]
        [JsonPropertyName("parent_holon_id")]
        public string? ParentHolonId { get; set; }

        [Column(Order = 5)]
        [References(typeof(Avatar), Optional = true)]
        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [Column(Order = 6, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("provider_name")]
        public string ProviderName { get; set; } = string.Empty;

        [Column(Order = 7, Type = "option<string>")]
        [JsonPropertyName("chain_id")]
        public string? ChainId { get; set; }

        [Column(Order = 8, Type = "option<string>")]
        [JsonPropertyName("asset_type")]
        public string? AssetType { get; set; }

        [Column(Order = 9, Type = "option<string>")]
        [JsonPropertyName("token_id")]
        public string? TokenId { get; set; }

        [Column(Order = 10, Type = "option<object>")]
        [FieldGroup("Metadata (flexible key->value bag)")]
        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [Column(Order = 11, Type = "option<array<string>>")]
        [FieldGroup("PeerHolonIds (list of holon-id strings)")]
        [JsonPropertyName("peer_holon_ids")]
        public IReadOnlyList<string>? PeerHolonIds { get; set; }

        // Order = 12 is the [ExtraSurrealField("embedding", ...)] declared at class level.

        [Column(Order = 13, Type = "datetime")]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 14, Type = "option<datetime>")]
        [JsonPropertyName("modified_date")]
        public DateTimeOffset? ModifiedDate { get; set; }

        [Column(Order = 15, Type = "bool")]
        [Default("true")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }
    }
}
