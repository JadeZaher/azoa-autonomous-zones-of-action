// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the platform_reserve_ledger table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("platform_reserve_ledger",
        Aggregate = "PlatformReserve (Services/Reserve/PlatformReserveService.cs)",
        Guardrail = "G6 SCHEMAFULL; one ledger row per (chain, network, asset) the platform reserve holds")]
    [SurrealNote("The node-side account of what the operator-owned reserve holds and has committed, per asset. It is NOT a balance ledger: chain truth is the balance. It records what the reserve has PROMISED out of each named partition, which chain state cannot express.")]
    [SurrealNote("partitions is a flexible object keyed by partition name (contributor, investor, or anything a tenant declares). Each value carries declared/issued/held/returned as exact decimal STRINGS so a supply larger than i64 is never truncated. Open by design: the partition vocabulary belongs to the tenant, so a closed enum would refuse legitimate future names.")]
    [SurrealNote("The oversubscription check runs here rather than only in a tenant database because sponsor exchanges and contributor holds draw from the SAME reserve. A partition policed in one caller's database can be overdrawn by the other caller.")]
    [SurrealNote("recent_draw_keys is a bounded FIFO replay window of applied draw operation keys. A retried draw whose key is still in the window is refused as a duplicate rather than double-counted; a key that has aged out of the window would be applied again, which is why callers anchor their own idempotency as well.")]
    [SurrealNote("version is the compare-and-swap anchor. Every partition move reads the row, applies the arithmetic in process, and writes back only while version is unchanged, so two concurrent draws cannot both spend the same availability.")]
    [Slice("identity")]
    [Index("platform_reserve_ledger_asset", Fields = new[] { "chain", "network", "asset_id" }, Unique = true)]
    [Index("platform_reserve_ledger_tenant", Fields = new[] { "tenant_id" })]
    public partial class PlatformReserveLedger : ISurrealRecord
    {
        public const string SchemaNameConst = "platform_reserve_ledger";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the SHA-256 of chain|network|asset id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("The asset this row accounts for, and where it lives")]
        [Required(NotEmpty = true)]
        public string AssetId { get; set; } = string.Empty;

        [Column(Order = 3)]
        [Required(NotEmpty = true)]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 4)]
        [Inside("Devnet", "Testnet", "Mainnet")]
        public string Network { get; set; } = string.Empty;

        /// <summary>Original protocol network identity when issuance supplied authoritative evidence.</summary>
        public string? ProtocolIdentity { get; set; }

        /// <summary>Reserve address that holds the asset; frozen here so a later key swap is visible.</summary>
        [Column(Order = 5)]
        [Required(NotEmpty = true)]
        public string ReserveAddress { get; set; } = string.Empty;

        /// <summary>Tenant that declared the asset, when one did. Null for the platform's own unit.</summary>
        [Column(Order = 6)]
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        [Column(Order = 7)]
        public string UnitName { get; set; } = string.Empty;

        [Column(Order = 8)]
        [Default("0")]
        public int Decimals { get; set; }

        /// <summary>Total supply in base units, as an exact decimal string (never truncated to i64).</summary>
        [Column(Order = 9)]
        [Required(NotEmpty = true)]
        public string TotalSupply { get; set; } = string.Empty;

        /// <summary>Named partitions: partition name to {declared, issued, held, returned} decimal strings.</summary>
        [Column(Order = 10, Flexible = true)]
        [FieldGroup("Named partitions -- the node-side oversubscription check")]
        public JsonElement? Partitions { get; set; }

        /// <summary>Bounded FIFO of recently applied draw operation keys; oldest first.</summary>
        [Column(Order = 11)]
        [FieldGroup("Replay window for draw operation keys")]
        public IReadOnlyList<string>? RecentDrawKeys { get; set; }

        [Column(Order = 12)]
        [FieldGroup("Optimistic concurrency + timestamps")]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 13)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 14)]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>Deterministic record id for one asset on one chain/network.</summary>
        public static string RecordIdFor(string chain, string network, string assetId)
        {
            var canonical =
                $"{chain.Trim().ToLowerInvariant()}|{network.Trim().ToLowerInvariant()}|{assetId.Trim()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }
    }
}
