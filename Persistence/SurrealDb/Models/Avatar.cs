// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the avatar table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("avatar",
        Aggregate = "Avatar (Models/Avatar.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Wallets navigation list is NOT persisted here — owned by IWalletStore via wallet.avatar_id FK.")]
    [SurrealNote("Tenant ownership (tenant-onboarding): owner_tenant_id is a self-FK to the tenant principal's avatar; external_user_id is the tenant's own user id for this child. The avatar_tenant_extuser composite UNIQUE is only meaningful for rows where BOTH owner_tenant_id AND external_user_id are non-NONE — same NULL-equals-NULL collision caveat the ApiKey POCO documents (ApiKey.cs:17). A tenant-managed row ALWAYS sets both (TenantManager.ProvisionChildAsync), so dedup-per-tenant is correct by construction; legacy/self-registered avatars leave both NONE and never collide.")]
    [Slice("identity")]
    [Index("avatar_username", Fields = new[] { "username" }, Unique = true)]
    [Index("avatar_email", Fields = new[] { "email" }, Unique = true)]
    [Index("avatar_owner_tenant", Fields = new[] { "owner_tenant_id" })]
    [Index("avatar_tenant_extuser", Fields = new[] { "owner_tenant_id", "external_user_id" }, Unique = true)]
    public partial class Avatar : ISurrealRecord
    {
        public const string SchemaNameConst = "avatar";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1, Type = "string")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("password_hash")]
        public string PasswordHash { get; set; } = string.Empty;

        [Column(Order = 5, Type = "option<string>")]
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [Column(Order = 6, Type = "option<string>")]
        [JsonPropertyName("first_name")]
        public string? FirstName { get; set; }

        [Column(Order = 7, Type = "option<string>")]
        [JsonPropertyName("last_name")]
        public string? LastName { get; set; }

        [Column(Order = 8, Type = "datetime")]
        [ReadOnly]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 9, Type = "option<datetime>")]
        [JsonPropertyName("last_beamed_in_date")]
        public DateTimeOffset? LastBeamedInDate { get; set; }

        [Column(Order = 10, Type = "bool")]
        [Default("true")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [Column(Order = 11, Type = "bool")]
        [Default("false")]
        [JsonPropertyName("is_verified")]
        public bool IsVerified { get; set; }

        [Column(Order = 12, Type = "option<record<avatar>>")]
        [References(typeof(Avatar), Optional = true)]
        [JsonPropertyName("owner_tenant_id")]
        public string? OwnerTenantId { get; set; }

        [Column(Order = 13, Type = "option<string>")]
        [JsonPropertyName("external_user_id")]
        public string? ExternalUserId { get; set; }

        [Column(Order = 14, Type = "option<string>")]
        [JsonPropertyName("external_ref")]
        public string? ExternalRef { get; set; }
    }
}
