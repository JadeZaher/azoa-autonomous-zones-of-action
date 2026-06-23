// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the star_odk table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
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

        public string? PublicKey { get; set; }

        public string? PrivateKeyHash { get; set; }

        [References(typeof(Avatar), Optional = true)]
        public string? AvatarId { get; set; }

        [FieldGroup("BoundHolonIds (list of holon-id strings)")]
        public IReadOnlyList<string>? BoundHolonIds { get; set; }

        public string? TargetChain { get; set; }

        public string? GeneratedCode { get; set; }

        public string? DeploymentConfig { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }

        [Default("true")]
        public bool IsActive { get; set; }
    }
}
