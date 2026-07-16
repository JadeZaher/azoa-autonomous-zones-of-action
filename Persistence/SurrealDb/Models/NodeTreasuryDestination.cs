// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_treasury_destination table.

#nullable enable

using System;
using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_treasury_destination",
        Aggregate = "NodeTreasuryDestination (Persistence/SurrealDb/Models/NodeTreasuryDestination.cs)",
        Guardrail = "G6 SCHEMAFULL; one versioned treasury destination per chain/network")]
    [SurrealNote("Treasury destinations are routing policy, separate from fee pricing. Settlements freeze the destination address and version they use.")]
    [Slice("identity")]
    [Index("node_treasury_destination_chain_network", Fields = new[] { "chain", "network" }, Unique = true)]
    public partial class NodeTreasuryDestination : ISurrealRecord
    {
        public const string SchemaNameConst = "node_treasury_destination";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [Required(NotEmpty = true)]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 3)]
        [Inside("Devnet", "Testnet", "Mainnet")]
        public string Network { get; set; } = string.Empty;

        [Column(Order = 4)]
        [Required(NotEmpty = true)]
        public string Address { get; set; } = string.Empty;

        [Column(Order = 5)]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 6)]
        [References(typeof(Avatar), Optional = true)]
        public string? UpdatedByAvatarId { get; set; }

        [Column(Order = 7)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 8)]
        public DateTimeOffset UpdatedAt { get; set; }

        public static string RecordIdFor(string chain, string network)
        {
            var canonical = $"{chain.Trim().ToLowerInvariant()}|{network.Trim().ToLowerInvariant()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }
    }
}
