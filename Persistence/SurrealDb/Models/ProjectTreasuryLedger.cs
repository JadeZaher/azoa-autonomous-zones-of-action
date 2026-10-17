// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the project_treasury_ledger table.

#nullable enable

using System.Security.Cryptography;
using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("project_treasury_ledger",
        Aggregate = "ProjectTreasury (Services/Reserve/ProjectTreasuryService.cs)",
        Guardrail = "G6 SCHEMAFULL; one custodial treasury account per (chain, network, tenant, project)")]
    [SurrealNote("The node-side account of ONE project's own custodial treasury: the account funding actually moves TO. It is NOT the node's fee destination -- that is node_treasury_destination, a different concept -- and it is NOT the platform reserve, which is the operator's single pooled account.")]
    [SurrealNote("treasury_address is the address DERIVED from the chain's platform key for this (chain, tenant, project) triple, frozen at first resolve. Every later resolve re-derives and compares: a platform-key rotation moves the derived address, and a mismatch FAILS CLOSED instead of quietly pointing the project at an account holding none of its value.")]
    [SurrealNote("assets is a flexible object keyed by asset id, each value carrying credited/spent/held as exact decimal STRINGS so an amount wider than i64 is never truncated. Available is credited - spent - held; the node refuses a spend or a hold that would take it negative.")]
    [SurrealNote("The counters are a record of COMMITMENT, not of chain balance. Chain truth remains the balance (data-engine guardrail); what chain state cannot express is how much of the balance is already promised to a payout that has not settled.")]
    [SurrealNote("recent_operation_keys is a bounded FIFO replay window of applied movement operation keys. A retried movement whose key is still in the window returns the counters unchanged rather than double-counting; a key that has aged out would apply again, which is why callers anchor their own idempotency as well.")]
    [SurrealNote("version is the compare-and-swap anchor. Every movement reads the row, applies the arithmetic in process, and writes back only while version is unchanged, so two concurrent spends cannot both consume the same availability.")]
    [SurrealNote("project_treasury_ledger_address is UNIQUE on purpose. Two projects sharing one derived address would mean the derivation lost part of its input, and this index turns that class of bug into a write failure instead of a silent pooling of two projects' money.")]
    [Slice("identity")]
    [Index("project_treasury_ledger_project",
        Fields = new[] { "chain", "network", "tenant_id", "project_reference" },
        Unique = true)]
    [Index("project_treasury_ledger_address",
        Fields = new[] { "chain", "network", "treasury_address" },
        Unique = true)]
    [Index("project_treasury_ledger_tenant", Fields = new[] { "tenant_id" })]
    public partial class ProjectTreasuryLedger : ISurrealRecord
    {
        public const string SchemaNameConst = "project_treasury_ledger";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core identity (record id is the SHA-256 of chain|network|tenant|project)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("The project this treasury belongs to, and where it lives")]
        [Required(NotEmpty = true)]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 3)]
        [Inside("Devnet", "Testnet", "Mainnet")]
        public string Network { get; set; } = string.Empty;

        /// <summary>Tenant that owns the project. A treasury is never a person, so no KYC gate applies to it.</summary>
        [Column(Order = 4)]
        [References(typeof(User))]
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Opaque tenant-side project id; the tenant's vocabulary, never parsed here.</summary>
        [Column(Order = 5)]
        [Required(NotEmpty = true)]
        public string ProjectReference { get; set; } = string.Empty;

        /// <summary>Derived custodial address, frozen so a later platform-key rotation is visible.</summary>
        [Column(Order = 6)]
        [FieldGroup("The derived custodial account (frozen so a platform-key rotation is visible)")]
        [Required(NotEmpty = true)]
        public string TreasuryAddress { get; set; } = string.Empty;

        /// <summary>Derivation rule that produced the address; changing it changes every address.</summary>
        [Column(Order = 7)]
        [Required(NotEmpty = true)]
        public string DerivationVersion { get; set; } = string.Empty;

        [Column(Order = 8)]
        [FieldGroup("Lifecycle")]
        [Inside("Open", "Closed")]
        public string Status { get; set; } = string.Empty;

        [Column(Order = 9)]
        public DateTimeOffset? ClosedAt { get; set; }

        /// <summary>Operator who closed the treasury; a close is always attributable.</summary>
        [Column(Order = 10)]
        [References(typeof(User), Optional = true)]
        public string? ClosedByUserId { get; set; }

        [Column(Order = 11)]
        public string? CloseReason { get; set; }

        /// <summary>Per-asset counters: asset id to {credited, spent, held} decimal strings.</summary>
        [Column(Order = 12, Flexible = true)]
        [FieldGroup("Per-asset counters -- credited, spent, held")]
        public JsonElement? Assets { get; set; }

        /// <summary>Bounded FIFO of recently applied movement operation keys; oldest first.</summary>
        [Column(Order = 13)]
        [FieldGroup("Replay window for movement operation keys")]
        public IReadOnlyList<string>? RecentOperationKeys { get; set; }

        [Column(Order = 14)]
        [FieldGroup("Optimistic concurrency + timestamps")]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 15)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 16)]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>Deterministic record id for one project's treasury on one chain/network.</summary>
        public static string RecordIdFor(string chain, string network, string tenantId, string projectReference)
        {
            var canonical =
                $"{chain.Trim().ToLowerInvariant()}|{network.Trim().ToLowerInvariant()}|" +
                $"{tenantId.Trim().ToLowerInvariant()}|{projectReference.Trim()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }
    }
}
