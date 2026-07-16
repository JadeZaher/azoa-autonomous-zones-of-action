// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_fee_audit table.

#nullable enable

using System;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_fee_audit",
        Aggregate = "NodeFeeAudit (Persistence/SurrealDb/Models/NodeFeeAudit.cs)",
        Guardrail = "G6 SCHEMAFULL; append-only node-fee audit")]
    [SurrealNote("Immutable audit trail for node fee schedule changes. Stores previous/new schedule snapshots as JSON strings so operators can reconstruct every version.")]
    [Slice("identity")]
    [Index("node_fee_audit_occurred", Fields = new[] { "occurred_at" })]
    [Index("node_fee_audit_actor", Fields = new[] { "actor_avatar_id" })]
    public partial class NodeFeeAudit : ISurrealRecord
    {
        public const string SchemaNameConst = "node_fee_audit";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [Inside("ScheduleUpdated")]
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
        public string? PreviousScheduleJson { get; set; }

        [Column(Order = 7)]
        [Required(NotEmpty = true)]
        public string ScheduleJson { get; set; } = string.Empty;

        [Column(Order = 8)]
        public string? Detail { get; set; }

        [Column(Order = 9)]
        [ReadOnly]
        public DateTimeOffset OccurredAt { get; set; }
    }
}
