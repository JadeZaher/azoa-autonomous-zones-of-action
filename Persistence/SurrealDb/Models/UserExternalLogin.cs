// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the user_external_login table (Phase 1B, D17/D18).

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Models.Auth;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("user_external_login",
        Aggregate = "UserExternalLogin (federated (provider, subject) -> user binding)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Binds a federated identity to a user. `subject` is the PROVIDER'S STABLE ID (Google `sub`, GitHub numeric `id`) and NEVER the email: emails are mutable, reassignable, and on some providers unverified, so keying on one lets a reassigned mailbox inherit an account.")]
    [SurrealNote("Uniqueness is (tenant_scope, provider, subject), modelled directly on kyc_identity's (tenant_scope, person_key). There is deliberately NO unique index on (provider, subject) alone: one Google account legitimately backs DIFFERENT users on different consumers, and a node-global binding would collapse those into one identity — the exact cross-tenant failure D1/D23 exist to prevent.")]
    [SurrealNote("This table NEVER satisfies KYC. It proves control of a provider account; kyc_identity proves a person. The value gate (Services/Kyc/KycGateService) reads kyc_submission + kyc_identity ONLY and has no read path to this table, so an OAuth-only user can never pass it.")]
    [SurrealNote("Deleting a row unlinks a provider and MUST NOT cascade to the user: the user row is independent and stays reachable through its other credentials (password, wallet, or another provider). ExternalLoginService refuses to unlink the last remaining credential rather than orphaning the account.")]
    [SurrealNote("email_at_link/display_name_at_link are AUDIT-ONLY snapshots of what the provider asserted at link time. Nothing reads them to resolve an account; the lookup key is (tenant_scope, provider, subject).")]
    [Slice("identity")]
    [Index("user_external_login_scope_provider_subject",
        Fields = new[] { "tenant_scope", "provider", "subject" },
        Unique = true)]
    [Index("user_external_login_by_user", Fields = new[] { "user_id" })]
    public partial class UserExternalLogin : ISurrealRecord
    {
        public const string SchemaNameConst = "user_external_login";
        public string SchemaName => SchemaNameConst;

        /// <summary>Deterministic digest of tenant_scope + provider + subject; upserts converge.</summary>
        [Id]
        [FieldGroup("Deterministic record id (SHA-256 of tenant_scope + provider + subject)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Uniqueness domain: a tenant Guid('N'), or the node-global sentinel.</summary>
        [FieldGroup("Uniqueness scope: per-tenant, mirroring kyc_identity (D23)")]
        [Required(NotEmpty = true)]
        public string TenantScope { get; set; } = string.Empty;

        /// <summary>Tenant user this binding belongs to; absent for node-direct sign-in.</summary>
        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>Which federated provider asserted this subject.</summary>
        [FieldGroup("Federated identity (provider's stable id — NEVER the email)")]
        [Inside("GOOGLE", "GITHUB")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ExternalAuthProvider Provider { get; set; }

        /// <summary>The provider's immutable subject identifier for this account.</summary>
        [Required(NotEmpty = true)]
        public string Subject { get; set; } = string.Empty;

        /// <summary>The user this federated identity signs in as.</summary>
        [FieldGroup("Bound user (unlink deletes THIS row only, never the user)")]
        [References(typeof(User))]
        [Required(NotEmpty = true)]
        public string UserId { get; set; } = string.Empty;

        /// <summary>Audit-only snapshot of the provider's asserted email at link time.</summary>
        [FieldGroup("Audit-only provider assertions (never a lookup key)")]
        public string? EmailAtLink { get; set; }

        /// <summary>Audit-only snapshot of the provider's asserted display name.</summary>
        public string? DisplayNameAtLink { get; set; }

        /// <summary>Database-defaulted and read-only; binding upserts omit it by design.</summary>
        [FieldGroup("Timestamps")]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset LinkedAt { get; set; }

        /// <summary>Last successful federated sign-in through this binding.</summary>
        public DateTimeOffset? LastLoginAt { get; set; }
    }
}
