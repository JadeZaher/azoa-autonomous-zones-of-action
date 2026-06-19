// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the api_key table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
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

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity (record id is the Guid('N') of ApiKey.Id)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Owner avatar (Guid('N') hex); indexed for ListByAvatar")]
        [References(typeof(Avatar))]
        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [FieldGroup("Caller-supplied label (free string)")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [FieldGroup("SHA-256(raw key) hex -- the dedup key; UNIQUE")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("key_hash")]
        public string KeyHash { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [FieldGroup("Display prefix (first 16 chars of raw key, never the secret)")]
        [JsonPropertyName("key_prefix")]
        public string KeyPrefix { get; set; } = string.Empty;

        [Column(Order = 6, Type = "datetime")]
        [FieldGroup("Creation timestamp (UTC)")]
        [ReadOnly]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 7, Type = "option<datetime>")]
        [FieldGroup("Optional expiry (NONE = no expiry)")]
        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [Column(Order = 8, Type = "option<datetime>")]
        [FieldGroup("Last-used timestamp (fire-and-forget; may lag under load)")]
        [JsonPropertyName("last_used_at")]
        public DateTimeOffset? LastUsedAt { get; set; }

        [Column(Order = 9, Type = "option<datetime>")]
        [FieldGroup("Soft-delete marker (NONE = active)")]
        [JsonPropertyName("revoked_at")]
        public DateTimeOffset? RevokedAt { get; set; }

        [Column(Order = 10, Type = "bool")]
        [FieldGroup("Active flag mirror for cheap WHERE filtering")]
        [Default("true")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [Column(Order = 11, Type = "option<string>")]
        [FieldGroup("Optional comma-separated scopes (empty/NONE = full access)")]
        [JsonPropertyName("scopes")]
        public string? Scopes { get; set; }
    }
}
