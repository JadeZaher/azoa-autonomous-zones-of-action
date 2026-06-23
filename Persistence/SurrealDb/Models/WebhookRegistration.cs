// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the webhook_registration table
// (tenant-consent-delegation §4, AC7 — per-tenant outbound webhook config).

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("webhook_registration",
        Aggregate = "WebhookRegistration (Models/WebhookRegistration.cs)",
        Guardrail = "G6 SCHEMAFULL, one active registration per tenant (unique tenant_id)")]
    [SurrealNote("A tenant's outbound consent-webhook config (tenant-consent-delegation §4, AC7): receiver url + per-tenant HMAC secret. ONE active registration per tenant for v1, enforced by the unique tenant_id index. Registration is scoped to the authenticated API-key principal — a tenant only ever reads/writes its OWN row (H5).")]
    [SurrealNote("url is https-only + public-IP-allowlisted, re-validated by WebhookSsrfGuard at DELIVERY time (not just registration) so a DNS rebind to a private address between register and deliver is still caught. secret is the per-tenant HMAC key (rotatable); each tenant's events are signed with ONLY its own secret — no shared secret.")]
    [SurrealNote("REQUIRED before real use: secret MUST be encrypted-at-rest (AES-256-GCM via WalletKeyService, mirroring Wallet.EncryptedPrivateKey) — the not-yet-built registration write-path encrypts before persisting and the delivery worker decrypts at read time. It is still persisted as-supplied today only because no write path exists yet. The API-facing model's Secret is [JsonIgnore]'d so it never leaves in an HTTP response; this storage column intentionally is not, since it must serialize to/from the DB.")]
    [Slice("bridge")]
    [Index("webhook_registration_tenant_unique", Fields = new[] { "tenant_id" }, Unique = true)]
    public partial class WebhookRegistration : ISurrealRecord
    {
        public const string SchemaNameConst = "webhook_registration";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owning tenant — unique, one active registration per tenant for v1")]
        [JsonPropertyName("tenant_id")]
        [References(typeof(Avatar))]
        public string TenantId { get; set; } = string.Empty;

        [FieldGroup("Receiver endpoint — https-only + public-IP, SSRF-guarded at delivery time")]
        [JsonPropertyName("url")]
        [Required(NotEmpty = true)]
        public string Url { get; set; } = string.Empty;

        // REQUIRED before webhooks ship with a real secret: encrypt-at-rest. This column
        // is the persisted form of the per-tenant HMAC key. It MUST hold an AES-256-GCM
        // ciphertext (WalletKeyService.EncryptPrivateKey), NOT the plaintext secret — the
        // not-yet-built registration write-path must encrypt before persisting and the
        // delivery worker must decrypt at read time. Mirrors Wallet.EncryptedPrivateKey.
        // NOTE: [JsonIgnore] is deliberately NOT applied here — this is the SurrealDB
        // storage POCO, so the column must serialize to/from the DB. The API-facing
        // model (Models/WebhookRegistration.cs) is the one that is [JsonIgnore]'d so the
        // secret never leaves in an HTTP response.
        [FieldGroup("Per-tenant HMAC secret (rotatable). REQUIRED: encrypt-at-rest like Wallet.EncryptedPrivateKey before real use.")]
        [JsonPropertyName("secret")]
        [Required(NotEmpty = true)]
        public string Secret { get; set; } = string.Empty;

        [FieldGroup("When the secret was last rotated (NONE if never rotated since creation)")]
        [JsonPropertyName("secret_rotated_at")]
        public DateTimeOffset? SecretRotatedAt { get; set; }

        [FieldGroup("Whether deliveries are active — inactive causes the worker to skip/dead-letter the tenant's events")]
        [JsonPropertyName("is_active")]
        [Default("true")]
        public bool IsActive { get; set; }

        [FieldGroup("Timestamps")]
        [JsonPropertyName("created_at")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
