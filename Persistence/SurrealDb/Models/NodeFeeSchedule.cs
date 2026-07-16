// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_fee_schedule table.

#nullable enable

using System;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_fee_schedule",
        Aggregate = "NodeFeeSchedule (Persistence/SurrealDb/Models/NodeFeeSchedule.cs)",
        Guardrail = "G6 SCHEMAFULL; node-local fee schedule")]
    [SurrealNote("Singleton local fee policy. Flat values are non-negative integer base-unit strings; bps values are 0..10000. Allocation Mint settles fees by netting the minted amount; nonzero Transfer fees remain fail-closed pending on-chain treasury settlement. Fees never create an off-chain balance.")]
    [Slice("identity")]
    public partial class NodeFeeSchedule : ISurrealRecord
    {
        public const string SchemaNameConst = "node_fee_schedule";
        public const string LocalId = "local";

        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = LocalId;

        [Column(Order = 2)]
        [Default("\"0\"")]
        [Assert("string::matches($value, \"^[0-9]+$\")")]
        public string MintFlatBaseUnits { get; set; } = "0";

        [Column(Order = 3)]
        [Default("0")]
        [Assert("$value >= 0 AND $value <= 10000")]
        public long MintBps { get; set; }

        [Column(Order = 4)]
        [Default("\"0\"")]
        [Assert("string::matches($value, \"^[0-9]+$\")")]
        public string TransferFlatBaseUnits { get; set; } = "0";

        [Column(Order = 5)]
        [Default("0")]
        [Assert("$value >= 0 AND $value <= 10000")]
        public long TransferBps { get; set; }

        [Column(Order = 6)]
        [Default("\"0\"")]
        [Assert("string::matches($value, \"^[0-9]+$\")")]
        public string SwapFlatBaseUnits { get; set; } = "0";

        [Column(Order = 7)]
        [Default("0")]
        [Assert("$value >= 0 AND $value <= 10000")]
        public long SwapBps { get; set; }

        [Column(Order = 8)]
        [Default("\"0\"")]
        [Assert("string::matches($value, \"^[0-9]+$\")")]
        public string QuestCompleteFlatBaseUnits { get; set; } = "0";

        [Column(Order = 9)]
        [Default("0")]
        [Assert("$value >= 0 AND $value <= 10000")]
        public long QuestCompleteBps { get; set; }

        [Column(Order = 10)]
        [Default("\"0\"")]
        [Assert("string::matches($value, \"^[0-9]+$\")")]
        public string FederationPublishFlatBaseUnits { get; set; } = "0";

        [Column(Order = 11)]
        [Default("0")]
        [Assert("$value >= 0 AND $value <= 10000")]
        public long FederationPublishBps { get; set; }

        [Column(Order = 12)]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 13)]
        [References(typeof(Avatar), Optional = true)]
        public string? UpdatedByAvatarId { get; set; }

        [Column(Order = 14)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 15)]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
