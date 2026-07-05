// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the consent_grant table (tenant-consent-delegation §1).

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("consent_grant",
        Aggregate = "ConsentGrant (Models/ConsentGrant.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("A user-granted, revocable authorization for a tenant to drive actions within Scopes. The signing seam does a LIVE validity check against this row before every tenant-driven key decrypt (AC4/AC5). revoked_at = NONE AND (expires_at = NONE OR now < expires_at) is the live predicate.")]
    [SurrealNote("Scopes stored as a CSV string (mirrors api_key.scopes). origin = UserExplicit|Participation. participation_ref is the opaque ArdaNova id; offboard revokes by (tenant_id, participation_ref) exact-match within the tenant's own grants only (L3).")]
    [Slice("identity")]
    [Index("consent_grant_by_grantor", Fields = new[] { "grantor_avatar_id" })]
    [Index("consent_grant_by_tenant", Fields = new[] { "tenant_id" })]
    [Index("consent_grant_seam_lookup", Fields = new[] { "grantor_avatar_id", "tenant_id" })]
    [Index("consent_grant_tenant_partref", Fields = new[] { "tenant_id", "participation_ref" })]
    public partial class ConsentGrant : ISurrealRecord
    {
        public const string SchemaNameConst = "consent_grant";
        public string SchemaName => SchemaNameConst;

        [Id]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("grantor_avatar_id")]
        [References(typeof(Avatar))]
        public string GrantorAvatarId { get; set; } = string.Empty;

        [JsonPropertyName("tenant_id")]
        [References(typeof(Avatar))]
        public string TenantId { get; set; } = string.Empty;

        [JsonPropertyName("scopes")]
        [Required(NotEmpty = true)]
        public string Scopes { get; set; } = string.Empty;

        [JsonPropertyName("origin")]
        [Inside("UserExplicit", "Participation")]
        [Default("\"UserExplicit\"")]
        public string Origin { get; set; } = "UserExplicit";

        [JsonPropertyName("participation_ref")]
        public string? ParticipationRef { get; set; }

        [JsonPropertyName("granted_at")]
        [ReadOnly]
        public DateTimeOffset GrantedAt { get; set; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonPropertyName("revoked_at")]
        public DateTimeOffset? RevokedAt { get; set; }
    }
}
