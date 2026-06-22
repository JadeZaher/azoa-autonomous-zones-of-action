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

        [Id]
        public string Id { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Username { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Email { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string PasswordHash { get; set; } = string.Empty;

        [Optional]
        public string? Title { get; set; }

        [Optional]
        public string? FirstName { get; set; }

        [Optional]
        public string? LastName { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? LastBeamedInDate { get; set; }

        [Default("true")]
        public bool IsActive { get; set; }

        [Default("false")]
        public bool IsVerified { get; set; }

        [References(typeof(Avatar), Optional = true)]
        public string? OwnerTenantId { get; set; }

        [Optional]
        public string? ExternalUserId { get; set; }

        [Optional]
        public string? ExternalRef { get; set; }
    }
}
