// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the oauth_exchange_code table (Phase 1B, D18).

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Models.Auth;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("oauth_exchange_code",
        Aggregate = "OAuthExchangeCode (single-use handoff between the OAuth redirect and the SPA)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Two purposes, one primitive. SESSION: the provider callback cannot hand a JWT to a single-page app without putting it in a URL (browser history, Referer, server logs), so the callback redirect carries a short-lived opaque code the SPA POSTs back for the real token. LINK: proves an ALREADY-AUTHENTICATED avatar explicitly asked to attach a provider, because a top-level OAuth redirect carries no Authorization header.")]
    [SurrealNote("Stores the SHA-256 of the code, never the code itself, exactly as ApiKey stores a hashed key: a database read must not yield a usable credential.")]
    [SurrealNote("Same anti-TOCTOU rule as wallet_auth_challenge: code_hash is UNIQUE and consumed atomically (consumed_at = NONE AND expires_at > now) so exactly one caller observes AffectedCount() == 1. Replaying a code returns NotFound, never a second session.")]
    [SurrealNote("tenant_scope is carried through the redirect so the exchange resolves the binding in the SAME scope the start call used; a code minted under one tenant can never mint a session in another.")]
    [Slice("identity")]
    [Index("oauth_exchange_code_hash", Fields = new[] { "code_hash" }, Unique = true)]
    [Index("oauth_exchange_code_expiry", Fields = new[] { "expires_at" })]
    public partial class OAuthExchangeCode : ISurrealRecord
    {
        public const string SchemaNameConst = "oauth_exchange_code";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Record id (Guid('N') of the minting)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>SHA-256 hex of the opaque code; the code itself is never persisted.</summary>
        [FieldGroup("Hashed single-use credential")]
        [Required(NotEmpty = true)]
        public string CodeHash { get; set; } = string.Empty;

        /// <summary>Whether this code mints a session or authorises a provider link.</summary>
        [Inside("SESSION", "LINK")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public OAuthExchangeCodePurpose Purpose { get; set; }

        /// <summary>Avatar the code resolves to (SESSION) or acts for (LINK).</summary>
        [FieldGroup("Subject + scope")]
        [References(typeof(Avatar))]
        [Required(NotEmpty = true)]
        public string AvatarId { get; set; } = string.Empty;

        /// <summary>Provider that authenticated (SESSION) or is being attached (LINK).</summary>
        [Inside("GOOGLE", "GITHUB")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ExternalAuthProvider Provider { get; set; }

        /// <summary>Uniqueness domain the start call ran under; carried through the redirect.</summary>
        [Required(NotEmpty = true)]
        public string TenantScope { get; set; } = string.Empty;

        /// <summary>Tenant avatar, when the sign-in was scoped to a consumer.</summary>
        [References(typeof(Avatar), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>Same-origin relative path the SPA should land on; never absolute.</summary>
        public string? ReturnTo { get; set; }

        [FieldGroup("Single-use lifecycle")]
        public DateTimeOffset ExpiresAt { get; set; }

        public DateTimeOffset? ConsumedAt { get; set; }

        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
