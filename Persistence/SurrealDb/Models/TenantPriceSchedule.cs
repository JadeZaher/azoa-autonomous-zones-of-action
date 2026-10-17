// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the tenant_price_schedule table (W4.2 -- the ARDA on-ramp price).

#nullable enable

using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("tenant_price_schedule",
        Aggregate = "TenantPriceSchedule (Services/Fiat/TenantPriceScheduleStore.cs)",
        Guardrail = "G6 SCHEMAFULL; one row per (scope, chain, asset, version) and rows are append-only")]
    [SurrealNote("One row is ONE VERSION of the price at which money buys units of one asset. Rows are never updated in place: a new price is a new version, so every issuance can name the exact row that priced it and an auditor can re-derive the quantity from the money years later. The unique index on (scope_key, chain_type, asset_id, version) is what makes the version sequence single-winner.")]
    [SurrealNote("units_per_minor_unit is the ONLY direction stored: base units of asset_id per ONE minor unit of currency_code. At ARDA's 2 decimals a base of 1 ARDA = $0.01 is \"100\" -- one cent buys one whole ARDA, which is 100 base units. It is a string so a money constant never passes through a floating-point binder. The D16 stamp needs the reciprocal (minor units per base unit) and derives it; the exact per-issuance truth stays on on_ramp_lot, which records both the money and the units.")]
    [SurrealNote("scope_key is who the price belongs to: a tenant's compact user id, or the literal \"node\" for the operator's own default. It is a plain string rather than a nullable record so the version index can be unique across both cases -- a NONE tenant_id would not participate in a unique index the way a value does.")]
    [SurrealNote("status is Active or Retired. A retired latest version means the tenant has STOPPED selling units at any price: the on-ramp refuses rather than reaching further back for an older Active row, because an older row is a price nobody is offering any more.")]
    [SurrealNote("declared_at is when the TENANT observed the price, not when this node stored it. The D16 declared-rate source refuses a declaration dated after the settlement it would price and one older than the configured window, so the two instants have to stay distinguishable.")]
    [Slice("identity")]
    [Index("tenant_price_schedule_version",
        Fields = new[] { "scope_key", "chain_type", "asset_id", "version" },
        Unique = true)]
    [Index("tenant_price_schedule_asset",
        Fields = new[] { "chain_type", "asset_id", "version" })]
    public partial class TenantPriceSchedule : ISurrealRecord
    {
        public const string SchemaNameConst = "tenant_price_schedule";
        public string SchemaName => SchemaNameConst;

        /// <summary>Scope key of the operator's own default schedule.</summary>
        public const string NodeScopeKey = "node";

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the SHA-256 of scope|chain|asset|version)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Whose price this is: a tenant's compact user id, or <c>node</c>.</summary>
        [Column(Order = 2)]
        [Required(NotEmpty = true)]
        public string ScopeKey { get; set; } = string.Empty;

        /// <summary>The tenant that registered it; absent on the operator's own default.</summary>
        [Column(Order = 3)]
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        [Column(Order = 4)]
        [Required(NotEmpty = true)]
        public string ChainType { get; set; } = string.Empty;

        /// <summary>The asset the money buys. Never blank: a price with no asset prices nothing.</summary>
        [Column(Order = 5)]
        [Required(NotEmpty = true)]
        public string AssetId { get; set; } = string.Empty;

        /// <summary>ISO-4217 code the money is in. USD only today (D15).</summary>
        [Column(Order = 6)]
        [FieldGroup("The price this version declares")]
        [Required(NotEmpty = true)]
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>Node-assigned, monotonic per (scope, chain, asset). Never caller-chosen.</summary>
        [Column(Order = 7)]
        public long Version { get; set; }

        /// <summary>Base units of the asset per ONE minor unit of the currency, as an exact decimal string.</summary>
        [Column(Order = 8)]
        [Required(NotEmpty = true)]
        public string UnitsPerMinorUnit { get; set; } = string.Empty;

        /// <summary>The registrant's own name for where the price came from; persisted verbatim into the stamp.</summary>
        [Column(Order = 9)]
        [Required(NotEmpty = true)]
        public string Source { get; set; } = string.Empty;

        /// <summary>Free-text operator note. Never interpreted.</summary>
        [Column(Order = 10)]
        public string? Note { get; set; }

        [Column(Order = 11)]
        [FieldGroup("Lifecycle")]
        [Inside("Active", "Retired")]
        [Default("\"Active\"")]
        public string Status { get; set; } = "Active";

        /// <summary>When the registrant observed this price -- not when this node stored it.</summary>
        [Column(Order = 12)]
        public DateTimeOffset DeclaredAt { get; set; }

        [Column(Order = 13)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset RegisteredAt { get; set; }

        /// <summary>The principal that registered this version.</summary>
        [Column(Order = 14)]
        [References(typeof(User))]
        public string RegisteredByUserId { get; set; } = string.Empty;

        /// <summary>Deterministic record id for one version of one scope's price for one asset.</summary>
        public static string RecordIdFor(string scopeKey, string chainType, string assetId, long version)
        {
            var canonical =
                $"{scopeKey.Trim().ToLowerInvariant()}|{chainType.Trim().ToLowerInvariant()}|" +
                $"{assetId.Trim()}|{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }
}
