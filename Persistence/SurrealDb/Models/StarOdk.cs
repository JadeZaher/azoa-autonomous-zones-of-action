// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the star_odk table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("star_odk",
        Aggregate = "STARODK (Models/STARODK.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Generated code + deployment config can be large strings; SurrealDB has no per-field size limit but operators should monitor row size.")]
    [Slice("identity")]
    [Index("star_avatar_id", Fields = new[] { "avatar_id" })]
    [Index("star_target_chain", Fields = new[] { "target_chain" })]
    [Index("star_is_active", Fields = new[] { "is_active" })]
    public partial class StarOdk : ISurrealRecord
    {
        public const string SchemaNameConst = "star_odk";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [Column(Order = 4, Type = "option<string>")]
        [JsonPropertyName("public_key")]
        public string? PublicKey { get; set; }

        [Column(Order = 5, Type = "option<string>")]
        [JsonPropertyName("private_key_hash")]
        public string? PrivateKeyHash { get; set; }

        [Column(Order = 6)]
        [References(typeof(Avatar), Optional = true)]
        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [Column(Order = 7, Type = "option<array<string>>")]
        [FieldGroup("BoundHolonIds (list of holon-id strings)")]
        [JsonPropertyName("bound_holon_ids")]
        public IReadOnlyList<string>? BoundHolonIds { get; set; }

        [Column(Order = 8, Type = "option<string>")]
        [JsonPropertyName("target_chain")]
        public string? TargetChain { get; set; }

        [Column(Order = 9, Type = "option<string>")]
        [JsonPropertyName("generated_code")]
        public string? GeneratedCode { get; set; }

        [Column(Order = 10, Type = "option<string>")]
        [JsonPropertyName("deployment_config")]
        public string? DeploymentConfig { get; set; }

        [Column(Order = 11, Type = "datetime")]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 12, Type = "option<datetime>")]
        [JsonPropertyName("modified_date")]
        public DateTimeOffset? ModifiedDate { get; set; }

        [Column(Order = 13, Type = "bool")]
        [Default("true")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }
    }
}
