// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the user table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("user",
        Aggregate = "User (Models/User.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Wallets navigation list is NOT persisted here — owned by IWalletStore via wallet.user_id FK.")]
    [SurrealNote("Tenant ownership (tenant-onboarding): owner_tenant_id is a self-FK to the tenant principal's user; external_user_id is the tenant's own user id for this child. The user_tenant_extuser composite UNIQUE is only meaningful for rows where BOTH owner_tenant_id AND external_user_id are non-NONE. A UNIQUE index does NOT collide NONE components -- verified empirically against SurrealDB 3.1.4, and explicit SQL NULL is unreachable under SCHEMAFULL option<T> -- so legacy/self-registered users that leave both NONE coexist freely and never contend. A tenant-managed row ALWAYS sets both (TenantManager.ProvisionChildAsync), so dedup-per-tenant is correct by construction. Earlier wording here inherited ApiKey.cs's 'NULL-equals-NULL collision' phrasing for a hazard that does not exist in this schema. See tests/AZOA.WebAPI.Tests/Kyc/SurrealUniqueIndexNoneSemanticsTests.cs.")]
    [Slice("identity")]
    [Index("user_username", Fields = new[] { "username" }, Unique = true)]
    [Index("user_email", Fields = new[] { "email" }, Unique = true)]
    [Index("user_owner_tenant", Fields = new[] { "owner_tenant_id" })]
    [Index("user_tenant_extuser", Fields = new[] { "owner_tenant_id", "external_user_id" }, Unique = true)]
    [Index("user_auth_wallet", Fields = new[] { "auth_wallet_address", "auth_wallet_chain_type" }, Unique = true)]
    public partial class User : ISurrealRecord
    {
        public const string SchemaNameConst = "user";
        public string SchemaName => SchemaNameConst;

        [Id]
        public string Id { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Username { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Email { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string PasswordHash { get; set; } = string.Empty;

        public string? Title { get; set; }

        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? LastBeamedInDate { get; set; }

        [Default("true")]
        public bool IsActive { get; set; }

        [Default("false")]
        public bool IsVerified { get; set; }

        [Required(NotEmpty = true)]
        [Default("\"dapp:user\"")]
        public string DappRole { get; set; } = "dapp:user";

        [References(typeof(User), Optional = true)]
        public string? OwnerTenantId { get; set; }

        public string? ExternalUserId { get; set; }

        public string? ExternalRef { get; set; }

        // ── user-sovereign-identity ────────────────────────────────────────────
        // AC2 wallet-challenge auth binding. The composite (auth_wallet_address,
        // auth_wallet_chain_type) is UNIQUE only for rows where BOTH are non-NONE
        // (same NULL-equals-NULL caveat as owner_tenant_id/external_user_id above):
        // an user with no wallet auth leaves both NONE and never collides. Login
        // matches this pair EXACTLY — never email/username/external_user_id.
        public string? AuthWalletAddress { get; set; }

        public string? AuthWalletChainType { get; set; }

        // AC3b/H2 post-claim custody-window cut. Tenant-driven tokens (child JWT /
        // claim token) minted before this instant are rejected at the signing seam.
        public DateTimeOffset? AuthNotBefore { get; set; }
    }
}
