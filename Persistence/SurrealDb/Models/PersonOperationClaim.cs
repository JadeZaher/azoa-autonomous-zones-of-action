// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the person_operation_claim table.

#nullable enable

using System;
using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("person_operation_claim",
        Aggregate = "PersonOperationClaim (node-operator-only per-operation Sybil ledger)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("NODE-OPERATOR-ONLY. Deliberately absent from SearchableEntityType, HolonQueryRequest, and the MCP user_scoped_query allow-list; see Persistence/SurrealDb/Models/AGENTS.md #person-operation-claim. A perfect denial message is worthless if a consumer can list this table and read a person key it did not create.")]
    [SurrealNote("Stores a person KEY and nothing else about the human. No first/last name, birthday, citizenship, or document field may ever be added here: a joinable attribute on this table would re-identify the pseudonymous claim ledger.")]
    [SurrealNote("Uniqueness is (uniqueness_scope, operation_key, person_key). uniqueness_scope is a tenant Guid('N') by default and the node-global sentinel only on an explicit per-operation opt-in (D23).")]
    [Slice("identity")]
    [Index("person_operation_claim_unique",
        Fields = new[] { "uniqueness_scope", "operation_key", "person_key" },
        Unique = true)]
    [Index("person_operation_claim_operation",
        Fields = new[] { "uniqueness_scope", "operation_key" })]
    public partial class PersonOperationClaim : ISurrealRecord
    {
        public const string SchemaNameConst = "person_operation_claim";
        public string SchemaName => SchemaNameConst;

        /// <summary>Deterministic digest of scope + operation key + person key; upserts converge.</summary>
        [Id]
        [FieldGroup("Deterministic record id (SHA-256 of uniqueness_scope + operation_key + person_key)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Uniqueness domain: a tenant Guid('N'), or the node-global sentinel.</summary>
        [FieldGroup("Claim identity: the (scope, operation, person) triple the unique index enforces")]
        [Required(NotEmpty = true)]
        public string UniquenessScope { get; set; } = string.Empty;

        /// <summary>Canonical, tenant-authored identifier of the claimable thing.</summary>
        [Required(NotEmpty = true)]
        public string OperationKey { get; set; } = string.Empty;

        /// <summary>
        /// The scope-appropriate pseudonymous person key. NOT a name, a document, or
        /// anything a consumer could join on.
        /// </summary>
        [Required(NotEmpty = true)]
        public string PersonKey { get; set; } = string.Empty;

        /// <summary>
        /// Holder of the claim, as a Guid('N') — the same shape
        /// <c>kyc_identity.user_ids</c> uses. This is the field the caller reads
        /// back to decide whether it won.
        /// </summary>
        [FieldGroup("Holder (read back to decide the refusal; never decided by a preceding read)")]
        public string? UserId { get; set; }

        /// <summary>Tenant the winning claim was made under; absent for node-direct flows.</summary>
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>When the current holder took the claim.</summary>
        [FieldGroup("Claim lifecycle")]
        public DateTimeOffset? ClaimedAt { get; set; }

        /// <summary>Set by compensation when the side effect failed; a released claim no longer blocks.</summary>
        public DateTimeOffset? ReleasedAt { get; set; }

        /// <summary>Database-defaulted and read-only; claim upserts omit it by design.</summary>
        [FieldGroup("Timestamps")]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }

        /// <summary>
        /// Deterministic record id for a claim triple, so an upsert converges on one
        /// row and the unique index and the record id agree. Mirrors the
        /// length-prefixed SHA-256 recipe of
        /// <c>KycPersonKeyDeriver.RecordId</c>; kept here rather than imported so a
        /// persistence POCO takes no dependency on a service.
        /// </summary>
        public static string RecordIdFor(string uniquenessScope, string operationKey, string personKey)
        {
            var digest = SHA256.HashData(BuildPreimage(uniquenessScope, operationKey, personKey));
            return Convert.ToHexString(digest).ToLowerInvariant()[..32];
        }

        /// <summary>
        /// Length-prefixed, separator-delimited preimage. A bare concatenation would
        /// let ("a", "bc") and ("ab", "c") collide, which here would silently merge
        /// two operations' uniqueness domains.
        /// </summary>
        private static byte[] BuildPreimage(string first, string second, string third)
        {
            var separator = new byte[] { 0x1F };
            var firstBytes = System.Text.Encoding.UTF8.GetBytes(first ?? string.Empty);
            var secondBytes = System.Text.Encoding.UTF8.GetBytes(second ?? string.Empty);
            var thirdBytes = System.Text.Encoding.UTF8.GetBytes(third ?? string.Empty);

            var buffer = new byte[12
                + firstBytes.Length + secondBytes.Length + thirdBytes.Length
                + (separator.Length * 2)];
            var offset = 0;

            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                buffer.AsSpan(offset), firstBytes.Length);
            offset += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                buffer.AsSpan(offset), secondBytes.Length);
            offset += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                buffer.AsSpan(offset), thirdBytes.Length);
            offset += 4;

            firstBytes.CopyTo(buffer, offset);
            offset += firstBytes.Length;
            separator.CopyTo(buffer, offset);
            offset += separator.Length;
            secondBytes.CopyTo(buffer, offset);
            offset += secondBytes.Length;
            separator.CopyTo(buffer, offset);
            offset += separator.Length;
            thirdBytes.CopyTo(buffer, offset);

            return buffer;
        }
    }
}
