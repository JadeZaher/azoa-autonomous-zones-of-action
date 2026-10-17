// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_identity table.

#nullable enable

using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Models.Kyc;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("kyc_identity",
        Aggregate = "KycIdentity (node-operator-only person<->user binding)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("NODE-OPERATOR-ONLY. Deliberately absent from SearchableEntityType, HolonQueryRequest, and the MCP user_scoped_query table allow-list; see Persistence/SurrealDb/Models/AGENTS.md #kyc-identity-binding.")]
    [SurrealNote("Stores a salted HMAC of document_number + issuing_state, never the raw document identifiers, and never address, personal_number, or any media/biometric material.")]
    [SurrealNote("Uniqueness is (tenant_scope, person_key). There is deliberately NO unique index on person_key alone: one human resolves to an unlinkable key per tenant (D23).")]
    [Slice("identity")]
    [Index("kyc_identity_scope_person",
        Fields = new[] { "tenant_scope", "person_key" },
        Unique = true)]
    [Index("kyc_identity_global_person", Fields = new[] { "global_person_key" })]
    [Index("kyc_identity_scope_users", Fields = new[] { "tenant_scope", "user_ids" })]
    [Index("kyc_identity_retention", Fields = new[] { "retention_expires_at" })]
    public partial class KycIdentity : ISurrealRecord
    {
        public const string SchemaNameConst = "kyc_identity";
        public string SchemaName => SchemaNameConst;

        /// <summary>Deterministic digest of tenant_scope + person_key; upserts converge.</summary>
        [Id]
        [FieldGroup("Deterministic record id (SHA-256 of tenant_scope + person_key)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Uniqueness domain: a tenant Guid('N'), or the node-global sentinel.</summary>
        [FieldGroup("Uniqueness scope: per-tenant by default, node-global only on explicit opt-in")]
        [Required(NotEmpty = true)]
        public string TenantScope { get; set; } = string.Empty;

        /// <summary>Tenant user this binding belongs to; absent for node-direct flows.</summary>
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>Per-tenant salted HMAC of document number + issuing state.</summary>
        [FieldGroup("Pseudonymous person keys (never the raw document identifiers)")]
        [Required(NotEmpty = true)]
        public string PersonKey { get; set; } = string.Empty;

        /// <summary>
        /// Node-scoped person key. Indexed but intentionally NOT unique: cross-tenant
        /// resolution is an explicit per-operation node-operator opt-in, never a
        /// constraint that would reject a second tenant's legitimate verification.
        /// </summary>
        [Required(NotEmpty = true)]
        public string GlobalPersonKey { get; set; } = string.Empty;

        /// <summary>Version of the derivation recipe, so a rotation is auditable.</summary>
        [Default("1")]
        public int PersonKeyVersion { get; set; } = 1;

        /// <summary>Users bound to this person within the scope; node-operator read only.</summary>
        [FieldGroup("Person -> users link (NEVER projected to a consumer)")]
        public IReadOnlyList<string>? UserIds { get; set; }

        /// <summary>Only these four personal fields are retained; cleared by erasure.</summary>
        [FieldGroup("Retained personal fields (the ONLY PII kept; tombstoned on erasure)")]
        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        public string? Birthday { get; set; }

        public string? Citizenship { get; set; }

        [FieldGroup("Provenance")]
        [Inside("MANUAL", "VERIFF", "GENERIC_HOSTED", "IDENTOMAT")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public KycProvider Provider { get; set; }

        public string ProviderKey { get; set; } = string.Empty;

        /// <summary>When the retained personal fields become erasable (D25).</summary>
        [FieldGroup("Retention + erasure (GDPR Art. 17 vs KYC record-keeping)")]
        public DateTimeOffset? RetentionExpiresAt { get; set; }

        /// <summary>Set when the four personal fields were tombstoned; keys survive.</summary>
        public DateTimeOffset? ErasedAt { get; set; }

        /// <summary>Non-personal operator note explaining the erasure trigger.</summary>
        public string? ErasureReason { get; set; }

        /// <summary>Database-defaulted and read-only; binding upserts omit it by design.</summary>
        [FieldGroup("Timestamps")]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }
    }
}
