// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the consent_audit table (tenant-consent-delegation AC10/L1).

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("consent_audit",
        Aggregate = "ConsentAuditEntry (Models/ConsentAuditEntry.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Append-only audit trail: one immutable row per grant, revoke, expiry, and tenant-driven sign decision (AC10/L1). Never updated or deleted. Independent of the best-effort webhook — this is the durable record of what AZOA decided.")]
    [Slice("identity")]
    [Index("consent_audit_by_tenant", Fields = new[] { "tenant_id", "occurred_at" })]
    [Index("consent_audit_by_grant", Fields = new[] { "grant_id" })]
    public partial class ConsentAudit : ISurrealRecord
    {
        public const string SchemaNameConst = "consent_audit";
        public string SchemaName => SchemaNameConst;

        [Id]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        [Inside("Granted", "Revoked", "Expired", "TenantSignAllowed", "TenantSignDenied")]
        [Required(NotEmpty = true)]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("grant_id")]
        public string? GrantId { get; set; }

        [JsonPropertyName("tenant_id")]
        [References(typeof(Avatar))]
        public string TenantId { get; set; } = string.Empty;

        [JsonPropertyName("avatar_id")]
        [References(typeof(Avatar), Optional = true)]
        public string? AvatarId { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("occurred_at")]
        [ReadOnly]
        public DateTimeOffset OccurredAt { get; set; }
    }
}
