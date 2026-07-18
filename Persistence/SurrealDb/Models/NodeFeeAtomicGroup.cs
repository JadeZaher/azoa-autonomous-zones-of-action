// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_fee_atomic_group table.

#nullable enable

using System;
using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_fee_atomic_group",
        Aggregate = "NodeFeeAtomicGroup (Persistence/SurrealDb/Models/NodeFeeAtomicGroup.cs)",
        Guardrail = "G6 SCHEMAFULL; immutable accepted two-leg chain group receipt")]
    [SurrealNote("One receipt is deterministically bound to one settlement. It records an accepted group only; independent chain observation remains required before terminalization.")]
    [Slice("bridge")]
    [Index("node_fee_atomic_group_settlement_unique", Fields = new[] { "settlement_id" }, Unique = true)]
    public partial class NodeFeeAtomicGroup : ISurrealRecord
    {
        public const string SchemaNameConst = "node_fee_atomic_group";
        public string SchemaName => SchemaNameConst;

        public enum StateKind
        {
            Submitted,
            PendingConfirmation,
            Confirmed,
        }

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [References(typeof(NodeFeeSettlement))]
        [ReadOnly]
        public string SettlementId { get; set; } = string.Empty;

        [Column(Order = 3)]
        [Required(NotEmpty = true)]
        [Assert("string::matches($value, \"^[0-9a-f]{64}$\")")]
        [ReadOnly]
        public string GroupIdentity { get; set; } = string.Empty;

        [Column(Order = 4)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string ChainGroupId { get; set; } = string.Empty;

        [Column(Order = 5)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string SourceAddress { get; set; } = string.Empty;

        [Column(Order = 6)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string PrimaryRecipientAddress { get; set; } = string.Empty;

        [Column(Order = 7)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string PrimaryTransactionId { get; set; } = string.Empty;

        [Column(Order = 8)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string TreasuryTransactionId { get; set; } = string.Empty;

        [Column(Order = 9)]
        [Inside("Submitted", "PendingConfirmation", "Confirmed")]
        [ReadOnly]
        public StateKind State { get; set; }

        [Column(Order = 10)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset AcceptedAt { get; set; }

        /// <summary>Derives the sole immutable receipt identifier for a settlement.</summary>
        public static string RecordIdFor(string settlementId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settlementId);
            return Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("node_fee_atomic_group|" + settlementId.Trim())))
                .ToLowerInvariant();
        }
    }
}
