// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_governance_audit table.

#nullable enable

using System;
using System.Collections.Generic;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_governance_audit",
        Aggregate = "NodeGovernanceAudit (Persistence/SurrealDb/Models/NodeGovernanceAudit.cs)",
        Guardrail = "G6 SCHEMAFULL; append-only node-governance audit")]
    [SurrealNote("Immutable audit trail for local node-governance policy changes. The store exposes no update/delete path.")]
    [Slice("identity")]
    [Index("node_governance_audit_occurred", Fields = new[] { "occurred_at" })]
    [Index("node_governance_audit_actor", Fields = new[] { "actor_avatar_id" })]
    public partial class NodeGovernanceAudit : ISurrealRecord
    {
        public const string SchemaNameConst = "node_governance_audit";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [Inside("ParametersUpdated")]
        [Required(NotEmpty = true)]
        public string Action { get; set; } = string.Empty;

        [Column(Order = 3)]
        [References(typeof(Avatar))]
        public string ActorAvatarId { get; set; } = string.Empty;

        [Column(Order = 4)]
        public long PreviousVersion { get; set; }

        [Column(Order = 5)]
        public long NewVersion { get; set; }

        [Column(Order = 6)]
        public IReadOnlyList<string>? PreviousAllowedChains { get; set; }

        [Column(Order = 7)]
        public IReadOnlyList<string>? PreviousAllowedAssetTypes { get; set; }

        [Column(Order = 8)]
        public IReadOnlyList<string>? AllowedChains { get; set; }

        [Column(Order = 9)]
        public IReadOnlyList<string>? AllowedAssetTypes { get; set; }

        [Column(Order = 10)]
        public string? Detail { get; set; }

        [Column(Order = 11)]
        [ReadOnly]
        public DateTimeOffset OccurredAt { get; set; }
    }
}
