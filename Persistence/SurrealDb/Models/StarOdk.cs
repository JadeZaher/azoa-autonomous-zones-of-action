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

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        [Optional]
        public string? PublicKey { get; set; }

        [Optional]
        public string? PrivateKeyHash { get; set; }

        [References(typeof(Avatar), Optional = true)]
        public string? AvatarId { get; set; }

        [Optional]
        [FieldGroup("BoundHolonIds (list of holon-id strings)")]
        public IReadOnlyList<string>? BoundHolonIds { get; set; }

        [Optional]
        public string? TargetChain { get; set; }

        [Optional]
        public string? GeneratedCode { get; set; }

        [Optional]
        public string? DeploymentConfig { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }

        [Default("true")]
        public bool IsActive { get; set; }
    }
}
