// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the hold_ledger table.

#nullable enable

using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("hold_ledger",
        Aggregate = "HoldCommand (Services/Hold/HoldCommandService.cs)",
        Guardrail = "G6 SCHEMAFULL; protocol, network, owner and tenant qualified custody liability")]
    [SurrealNote("Immutable definition and opening command bind liability; atomic command receipts preserve replay independently of later state. See Services/Hold/AGENTS.md.")]
    [Slice("identity")]
    [Index("hold_ledger_hold",
        Fields = new[] { "chain", "network", "protocol_identity", "owner_user_id", "tenant_id", "hold_id" },
        Unique = true)]
    [Index("hold_ledger_due", Fields = new[] { "chain", "network", "state", "release_not_before" })]
    [Index("hold_ledger_beneficiary", Fields = new[] { "beneficiary_user_id" })]
    [Index("hold_ledger_funder", Fields = new[] { "funder_user_id" })]
    public partial class HoldLedger : ISurrealRecord
    {
        public const string SchemaNameConst = "hold_ledger";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1)]
        [FieldGroup("Core custody identity")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Original owner-scoped reference within the physical custody identity.</summary>
        [Column(Order = 2)]
        [Required(NotEmpty = true)]
        public string HoldId { get; set; } = string.Empty;

        [Column(Order = 3)]
        [Required(NotEmpty = true)]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 4)]
        [Inside("Devnet", "Testnet", "Mainnet")]
        public string Network { get; set; } = string.Empty;

        /// <summary>Original physical protocol identity; absent historical rows require provenance recovery.</summary>
        public string? ProtocolIdentity { get; set; }
        public string? OwnerUserId { get; set; }
        public string? DefinitionHash { get; set; }
        public string? DefinitionJson { get; set; }
        public string? OpeningCommandId { get; set; }
        public string? ActiveCommandId { get; set; }
        public string? ActiveReleaseMandateId { get; set; }
        public string? EconomicSourceId { get; set; }

        /// <summary>Custodial account the units sit in; frozen so a later key change is visible.</summary>
        [Column(Order = 5)]
        [FieldGroup("What is held, where, and out of which declared budget")]
        [Required(NotEmpty = true)]
        public string HoldingAddress { get; set; } = string.Empty;

        [Column(Order = 6)]
        [Required(NotEmpty = true)]
        public string AssetId { get; set; } = string.Empty;

        /// <summary>Amount in the asset's base units, as an exact decimal string (never truncated to i64).</summary>
        [Column(Order = 7)]
        [Required(NotEmpty = true)]
        public string Amount { get; set; } = string.Empty;

        /// <summary>Named reserve partition the commitment is drawn from.</summary>
        [Column(Order = 8)]
        public string ReservePartition { get; set; } = string.Empty;

        [Column(Order = 9)]
        [Inside("Reserve", "Funder")]
        public string FundingSource { get; set; } = string.Empty;

        /// <summary>Person who funded the hold, when a person did. Absent on a reserve-funded hold.</summary>
        [Column(Order = 10)]
        [FieldGroup("Who it is for")]
        [References(typeof(User), Optional = true)]
        public string? FunderUserId { get; set; }

        /// <summary>Person the units are held FOR; the only account a release may pay.</summary>
        [Column(Order = 11)]
        [References(typeof(User))]
        public string BeneficiaryUserId { get; set; } = string.Empty;

        /// <summary>Tenant that drove the hold, when one did; null for a user-driven hold.</summary>
        [Column(Order = 12)]
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>Caller's own reason for the hold; carried through to the operation log.</summary>
        [Column(Order = 13)]
        public string? Purpose { get; set; }

        [Column(Order = 14)]
        [FieldGroup("Lifecycle")]
        [Inside("Pending", "AwaitingSourceSettlement", "Held", "Releasing", "Released", "Refunded", "ReleasedIntoHold", "Cancelled", "PartiallyRecovered")]
        public string State { get; set; } = string.Empty;

        /// <summary>The hold window: a plain release before this instant is refused.</summary>
        [Column(Order = 15)]
        public DateTimeOffset? ReleaseNotBefore { get; set; }

        /// <summary>True when the hosted sweep may release this hold once it is due.</summary>
        [Column(Order = 16)]
        [Default("false")]
        public bool AutoRelease { get; set; }

        /// <summary>True when the parent hold already took the partition draw for these units.</summary>
        [Column(Order = 17)]
        [Default("false")]
        public bool InheritsReserveCommitment { get; set; }

        /// <summary>The hold this one continues, when it continues one.</summary>
        [Column(Order = 18)]
        public string? ParentHoldId { get; set; }

        /// <summary>The hold this one was released into, when it was.</summary>
        [Column(Order = 19)]
        public string? ChildHoldId { get; set; }

        /// <summary>Rail reference for the outbound move; read before any re-send.</summary>
        [Column(Order = 20)]
        [FieldGroup("Rail evidence and settlement")]
        public string? SettlementTransactionId { get; set; }

        [Column(Order = 21)]
        public DateTimeOffset? SettledAt { get; set; }

        /// <summary>Why the hold ended the way it did; always present on a terminal row.</summary>
        [Column(Order = 22)]
        public string? SettlementReason { get; set; }

        [Column(Order = 23)]
        [FieldGroup("Optimistic concurrency + timestamps")]
        [Default("0")]
        public long Version { get; set; }

        [Column(Order = 24)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 25)]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>Legacy lookup identity; new admission uses HoldCommandIdentity.</summary>
        public static string RecordIdFor(string chain, string network, string holdId)
        {
            var canonical =
                $"{chain.Trim().ToLowerInvariant()}|{network.Trim().ToLowerInvariant()}|{holdId.Trim()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }
    }
}
