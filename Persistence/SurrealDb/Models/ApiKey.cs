// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the api_key table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("api_key",
        Aggregate = "ApiKey (Models/ApiKey.cs)",
        Guardrail = "G6 SCHEMAFULL, G2 UNIQUE-on-key_hash insert-wins lookup")]
    [SurrealNote("Authentication ledger. The handler hashes the inbound X-Api-Key header (SHA-256) and looks up key_hash via the unique index. The raw key never reaches storage. key_prefix is the first 16 chars of the raw key for caller display only.")]
    [SurrealNote("B3 nullable-collision review: api_key_unique_hash UNIQUE on key_hash is NOT NULL (ASSERT != NONE AND != \"\"), so SurrealDB's NULL-equals-NULL collision semantics are not in play -- correct dedup by construction.")]
    [SurrealNote("Soft-delete via revoked_at + is_active. Handlers MUST check both is_active AND revoked_at IS NONE AND (expires_at IS NONE OR expires_at > now) before granting authentication. last_used_at is updated fire-and-forget from the handler -- TouchLastUsedAsync must never throw.")]
    [Slice("identity")]
    [Index("api_key_unique_hash", Fields = new[] { "key_hash" }, Unique = true)]
    [Index("api_key_by_avatar", Fields = new[] { "avatar_id" })]
    public partial class ApiKey : ISurrealRecord
    {
        public const string SchemaNameConst = "api_key";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of ApiKey.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owner avatar (Guid('N') hex); indexed for ListByAvatar")]
        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [FieldGroup("Caller-supplied label (free string)")]
        public string Name { get; set; } = string.Empty;

        [FieldGroup("SHA-256(raw key) hex -- the dedup key; UNIQUE")]
        [Required(NotEmpty = true)]
        public string KeyHash { get; set; } = string.Empty;

        [FieldGroup("Display prefix (first 16 chars of raw key, never the secret)")]
        public string KeyPrefix { get; set; } = string.Empty;

        [FieldGroup("Creation timestamp (UTC)")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        [FieldGroup("Optional expiry (NONE = no expiry)")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [FieldGroup("Last-used timestamp (fire-and-forget; may lag under load)")]
        public DateTimeOffset? LastUsedAt { get; set; }

        [FieldGroup("Soft-delete marker (NONE = active)")]
        public DateTimeOffset? RevokedAt { get; set; }

        [FieldGroup("Active flag mirror for cheap WHERE filtering")]
        [Default("true")]
        public bool IsActive { get; set; }

        [FieldGroup("Optional comma-separated scopes (empty/NONE = full access)")]
        public string? Scopes { get; set; }
    }
}
