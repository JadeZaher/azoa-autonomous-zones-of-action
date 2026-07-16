// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_governance_parameters table.

#nullable enable

using System;
using System.Collections.Generic;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_governance_parameters",
        Aggregate = "NodeGovernanceParameters (Persistence/SurrealDb/Models/NodeGovernanceParameters.cs)",
        Guardrail = "G6 SCHEMAFULL; node-governed runtime allowlists")]
    [SurrealNote("Singleton local node policy. Null allowlist = unrestricted dimension; empty array = deny all values in that dimension. Runtime enforcement lives in NodeGovernanceGuard.")]
    [Slice("identity")]
    public partial class NodeGovernanceParameters : ISurrealRecord
    {
        public const string SchemaNameConst = "node_governance_parameters";
        public const string LocalId = "local";

        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = LocalId;

        [Column(Order = 2)]
        [FieldGroup("Allowed chain names; null = unrestricted, empty = deny all")]
        public IReadOnlyList<string>? AllowedChains { get; set; }

        [Column(Order = 3)]
        [FieldGroup("Allowed governed asset type names; null = unrestricted, empty = deny all")]
        public IReadOnlyList<string>? AllowedAssetTypes { get; set; }

        [Column(Order = 4)]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 5)]
        [References(typeof(Avatar), Optional = true)]
        public string? UpdatedByAvatarId { get; set; }

        [Column(Order = 6)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 7)]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
