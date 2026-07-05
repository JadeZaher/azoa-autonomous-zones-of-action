// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the wallet_auth_claim_token table (user-sovereign-identity §2, AC3/AC4).

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("wallet_auth_claim_token",
        Aggregate = "WalletAuthClaimToken (Models/WalletAuthClaimToken.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Tenant-minted single-use, short-TTL claim-invite token (AC4). A tenant generates it for a child it owns; the user redeems it with a USER-SIDE credential (fresh wallet challenge OR user-chosen password). The token alone NEVER sets a credential -- it only authorizes the credential-setting step (M1).")]
    [SurrealNote("Same anti-TOCTOU rule as the auth nonce: token is UNIQUE and consumed atomically (consumed_at = NONE AND expires_at > now) at a winning claim. target_avatar_id is the child being claimed; tenant_id is the minting tenant (asserted owner at mint time). Cross-tenant redemption resolves to NotFound (isolation crux).")]
    [Slice("identity")]
    [Index("wallet_auth_claim_token_token", Fields = new[] { "token" }, Unique = true)]
    [Index("wallet_auth_claim_token_by_target", Fields = new[] { "target_avatar_id" })]
    public partial class WalletAuthClaimToken : ISurrealRecord
    {
        public const string SchemaNameConst = "wallet_auth_claim_token";
        public string SchemaName => SchemaNameConst;

        [Id]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        [Required(NotEmpty = true)]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("target_avatar_id")]
        [References(typeof(Avatar))]
        public string TargetAvatarId { get; set; } = string.Empty;

        [JsonPropertyName("tenant_id")]
        [References(typeof(Avatar))]
        public string TenantId { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("consumed_at")]
        public DateTimeOffset? ConsumedAt { get; set; }

        [JsonPropertyName("created_at")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
