// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_treasury_audit table.

#nullable enable

using System;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_treasury_audit",
        Aggregate = "NodeTreasuryAudit (Persistence/SurrealDb/Models/NodeTreasuryAudit.cs)",
        Guardrail = "G6 SCHEMAFULL; append-only node-treasury audit")]
    [SurrealNote("Immutable audit trail for chain/network treasury routing changes. The store exposes no update or delete path.")]
    [Slice("identity")]
    [Index("node_treasury_audit_occurred", Fields = new[] { "occurred_at" })]
    [Index("node_treasury_audit_actor", Fields = new[] { "actor_avatar_id" })]
    [Index("node_treasury_audit_chain_network", Fields = new[] { "chain", "network" })]
    public partial class NodeTreasuryAudit : ISurrealRecord
    {
        public const string SchemaNameConst = "node_treasury_audit";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [Inside("DestinationUpdated")]
        public string Action { get; set; } = string.Empty;

        [Column(Order = 3)]
        [References(typeof(Avatar))]
        public string ActorAvatarId { get; set; } = string.Empty;

        [Column(Order = 4)]
        [Required(NotEmpty = true)]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 5)]
        [Inside("Devnet", "Testnet", "Mainnet")]
        public string Network { get; set; } = string.Empty;

        [Column(Order = 6)]
        public long PreviousVersion { get; set; }

        [Column(Order = 7)]
        public long NewVersion { get; set; }

        [Column(Order = 8)]
        public string? PreviousDestinationJson { get; set; }

        [Column(Order = 9)]
        [Required(NotEmpty = true)]
        public string DestinationJson { get; set; } = string.Empty;

        [Column(Order = 10)]
        public string? Detail { get; set; }

        [Column(Order = 11)]
        [ReadOnly]
        public DateTimeOffset OccurredAt { get; set; }
    }
}
