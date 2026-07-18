// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_fee_settlement table.

#nullable enable

using System;
using System.Globalization;
using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("node_fee_settlement",
        Aggregate = "NodeFeeSettlement (Persistence/SurrealDb/Models/NodeFeeSettlement.cs)",
        Guardrail = "G6 SCHEMAFULL; durable, version-pinned fee-settlement intent")]
    [SurrealNote("An inert settlement boundary for fee consumers that require a separate treasury effect. The deterministic id is derived from the parent idempotency key and operation, while the row freezes pricing and treasury routing before either effect can be submitted.")]
    [Slice("bridge")]
        [Index("node_fee_settlement_parent_operation", Fields = new[] { "parent_idempotency_key_hash", "operation" }, Unique = true)]
        [Index("node_fee_settlement_recovery_due", Fields = new[] { "state", "next_attempt_at" })]
        [Index("node_fee_settlement_lease_expiry", Fields = new[] { "lease_expires_at" })]
    public partial class NodeFeeSettlement : ISurrealRecord
    {
        public const string SchemaNameConst = "node_fee_settlement";
        public const string ParentClaimOperationPrefix = "node_fee_settlement/";
        public string SchemaName => SchemaNameConst;

        public enum StateKind
        {
            Prepared,
            PrimarySubmitted,
            FeeSubmitted,
            AwaitingReconciliation,
            Settled,
            Cancelled,
        }

        public enum EffectStateKind
        {
            NotStarted,
            Submitted,
            Confirmed,
            Failed,
            Unknown,
        }

        [Id, Column(Order = 1)]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [Required(NotEmpty = true)]
        [Assert("string::matches($value, \"^[0-9a-f]{64}$\")")]
        [ReadOnly]
        public string ParentIdempotencyKeyHash { get; set; } = string.Empty;

        [Column(Order = 3)]
        [Inside("Mint", "Transfer", "Swap", "QuestComplete", "FederationPublish")]
        [ReadOnly]
        public string Operation { get; set; } = string.Empty;

        [Column(Order = 4)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string Chain { get; set; } = string.Empty;

        [Column(Order = 5)]
        [Inside("Devnet", "Testnet", "Mainnet")]
        [ReadOnly]
        public string Network { get; set; } = string.Empty;

        [Column(Order = 6)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string AssetId { get; set; } = string.Empty;

        [Column(Order = 7)]
        [Assert("string::matches($value, \"^[1-9][0-9]{0,19}$\") AND type::decimal($value) <= 18446744073709551615dec")]
        [ReadOnly]
        public string GrossAmount { get; set; } = string.Empty;

        [Column(Order = 8)]
        [Assert("string::matches($value, \"^[1-9][0-9]{0,19}$\") AND type::decimal($value) <= 18446744073709551615dec")]
        [ReadOnly]
        public string FeeAmount { get; set; } = string.Empty;

        [Column(Order = 9)]
        [Assert("string::matches($value, \"^[1-9][0-9]{0,19}$\") AND type::decimal($value) <= 18446744073709551615dec")]
        [ReadOnly]
        public string NetAmount { get; set; } = string.Empty;

        [Column(Order = 10)]
        [Default("0")]
        [Assert("$value >= 0")]
        [ReadOnly]
        public long FeeScheduleVersion { get; set; }

        [Column(Order = 11)]
        [Required(NotEmpty = true)]
        [ReadOnly]
        public string TreasuryAddress { get; set; } = string.Empty;

        [Column(Order = 12)]
        [Default("0")]
        [Assert("$value >= 0")]
        [ReadOnly]
        public long TreasuryDestinationVersion { get; set; }

        [Column(Order = 13)]
        [Inside("Prepared", "PrimarySubmitted", "FeeSubmitted", "AwaitingReconciliation", "Settled", "Cancelled")]
        [Default("\"Prepared\"")]
        public StateKind State { get; set; } = StateKind.Prepared;

        [Column(Order = 14)]
        [Inside("NotStarted", "Submitted", "Confirmed", "Failed", "Unknown")]
        [Default("\"NotStarted\"")]
        public EffectStateKind PrimaryEffectState { get; set; } = EffectStateKind.NotStarted;

        [Column(Order = 15)]
        [Inside("NotStarted", "Submitted", "Confirmed", "Failed", "Unknown")]
        [Default("\"NotStarted\"")]
        public EffectStateKind FeeEffectState { get; set; } = EffectStateKind.NotStarted;

        [Column(Order = 16)]
        [References(typeof(OperationLog), Optional = true)]
        public string? PrimaryOperationId { get; set; }

        [Column(Order = 17)]
        [References(typeof(OperationLog), Optional = true)]
        public string? FeeOperationId { get; set; }

        [Column(Order = 18)]
        public string? PrimaryTransactionHash { get; set; }

        [Column(Order = 19)]
        public string? FeeTransactionHash { get; set; }

        [Column(Order = 20)]
        [Default("0")]
        [Assert("$value >= 0")]
        public long StateVersion { get; set; }

        [Column(Order = 21)]
        [Default("0")]
        [Assert("$value >= 0")]
        public long AttemptCount { get; set; }

        [Column(Order = 22)]
        public DateTimeOffset NextAttemptAt { get; set; }

        [Column(Order = 23)]
        public string? LeaseToken { get; set; }

        [Column(Order = 24)]
        public DateTimeOffset? LeaseExpiresAt { get; set; }

        [Column(Order = 25)]
        public string? ReconciliationReason { get; set; }

        [Column(Order = 26)]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 27)]
        public DateTimeOffset UpdatedAt { get; set; }

        [Column(Order = 28)]
        [Assert("$value = NONE OR string::matches($value, \"^[0-9a-f]{64}$\")")]
        [ReadOnly]
        public string? ExpectedAtomicGroupIdentity { get; set; }

        public static string HashParentIdempotencyKey(string parentIdempotencyKey)
        {
            var canonicalKey = CanonicalizeParentIdempotencyKey(parentIdempotencyKey);
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalKey)))
                .ToLowerInvariant();
        }

        /// <summary>Returns the sole canonical representation used for parent claim identity.</summary>
        public static string CanonicalizeParentIdempotencyKey(string parentIdempotencyKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentIdempotencyKey);
            return parentIdempotencyKey.Trim();
        }

        public static string RecordIdFor(string parentIdempotencyKey, string operation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            var canonical = $"{HashParentIdempotencyKey(parentIdempotencyKey)}|{operation.Trim()}";
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        public static string ParentClaimOperationType(string operation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            return ParentClaimOperationPrefix + operation.Trim();
        }

        /// <summary>Parses a canonical positive unsigned 64-bit base-unit amount.</summary>
        public static bool TryParseCanonicalPositiveBaseUnitAmount(string? value, out ulong amount)
        {
            amount = 0;
            if (value is null || value.Length is 0 or > 20 || value[0] is < '1' or > '9')
                return false;

            foreach (var character in value)
            {
                if (character is < '0' or > '9')
                    return false;
            }

            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out amount);
        }
    }
}
