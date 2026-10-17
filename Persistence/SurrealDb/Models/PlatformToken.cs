// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the platform_token table.

#nullable enable

using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("platform_token",
        Aggregate = "PlatformTokenGenesis (Services/Reserve/PlatformTokenGenesis.cs)",
        Guardrail = "G6 SCHEMAFULL; one immutable platform definition per network")]
    [SurrealNote("Finalized creation authority, reserve registration and activation commit atomically; see Services/Reserve/AGENTS.md.")]
    [Slice("identity")]
    [Index("platform_token_scope", Fields = new[] { "chain", "network" }, Unique = true)]
    [Index("platform_token_network", Fields = new[] { "network" }, Unique = true)]
    [Index("platform_token_network_status", Fields = new[] { "network", "status" })]
    public partial class PlatformToken : ISurrealRecord
    {
        public const string SchemaNameConst = "platform_token";

        /// <summary>Claimed definition awaiting finalized issuance and accounting.</summary>
        public const string StatusPending = "Pending";

        /// <summary>Recorded activation; verified reads also require exact issuance lineage.</summary>
        public const string StatusActive = "Active";

        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the SHA-256 of chain|network)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Where the platform unit lives")]
        [Required(NotEmpty = true)]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 3)]
        [Inside("Devnet", "Testnet", "Mainnet")]
        public string Network { get; set; } = string.Empty;

        [Column(Order = 4)]
        [FieldGroup("Genesis lifecycle -- Pending awaits final projection, Active records activation")]
        [Inside("Pending", "Active")]
        public string Status { get; set; } = StatusPending;

        /// <summary>Finalized chain asset identity; absent until atomic activation.</summary>
        [Column(Order = 5)]
        public string? AssetId { get; set; }

        [Column(Order = 6)]
        [FieldGroup("The frozen genesis definition -- a change here means a SECOND asset, not an edit")]
        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 7)]
        [Required(NotEmpty = true)]
        public string UnitName { get; set; } = string.Empty;

        [Column(Order = 8)]
        [Default("0")]
        public int Decimals { get; set; }

        /// <summary>Total supply in base units, as an exact decimal string (never truncated to i64).</summary>
        [Column(Order = 9)]
        [Required(NotEmpty = true)]
        public string TotalSupply { get; set; } = string.Empty;

        /// <summary>Reserve address that created and holds the asset, frozen so a later key swap is visible.</summary>
        [Column(Order = 10)]
        [FieldGroup("Provenance of the one irreversible act")]
        [Required(NotEmpty = true)]
        public string ReserveAddress { get; set; } = string.Empty;

        /// <summary>The deterministic key the mint is anchored on; a resumed genesis reuses it verbatim.</summary>
        [Column(Order = 11)]
        [Required(NotEmpty = true)]
        public string GenesisOperationKey { get; set; } = string.Empty;

        public string? CreationOperationId { get; set; }

        /// <summary>The node operator who ran genesis.</summary>
        [Column(Order = 12)]
        [References(typeof(User))]
        public string GenesisUserId { get; set; } = string.Empty;

        /// <summary>Why the last mint attempt did not complete; null once the row is Active.</summary>
        [Column(Order = 13)]
        public string? LastFailureReason { get; set; }

        [Column(Order = 14)]
        [FieldGroup("Optimistic concurrency + timestamps")]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 15)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset ClaimedAt { get; set; }

        [Column(Order = 16)]
        public DateTimeOffset? ActivatedAt { get; set; }

        [Column(Order = 17)]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>Whether activation is recorded; this alone does not verify issuance lineage.</summary>
        public bool IsActive =>
            string.Equals(Status, StatusActive, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(AssetId);

        /// <summary>Deterministic record id for the platform unit on one chain/network.</summary>
        public static string RecordIdFor(string chain, string network)
        {
            var canonical =
                $"{chain.Trim().ToLowerInvariant()}|{network.Trim().ToLowerInvariant()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }
    }
}
