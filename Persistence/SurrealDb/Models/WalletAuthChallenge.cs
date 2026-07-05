// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the wallet_auth_challenge table (user-sovereign-identity §1).

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("wallet_auth_challenge",
        Aggregate = "WalletAuthChallenge (Models/WalletAuthChallenge.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Single-use wallet-challenge auth nonce (AC1/AC1b). The user signs the EXACT domain_message bytes; verify re-validates every field server-side then atomically consumes via a conditional UPDATE (consumed_at = NONE AND expires_at > now). nonce is UNIQUE -- the no-TOCTOU single-use primitive: two concurrent verifies of one nonce yield exactly one success.")]
    [SurrealNote("domain_message is the literal human-readable bytes-to-sign (AZOA-AUTH-v1 prefix + issuer/audience + chain_type + address + nonce + expiry). Stored verbatim so the server is the single source of truth for the signed payload; verify never trusts a client-supplied message except as an exact-equality cross-check against this column.")]
    [SurrealNote("Bound to (address, chain_type); the by-address index serves the latest-live lookup. consumed_at = NONE means unconsumed; a row is dead once consumed_at != NONE OR expires_at <= now. Greenfield: expired/consumed rows may be reaped by a future janitor -- not required for correctness given the atomic consume.")]
    [Slice("identity")]
    [Index("wallet_auth_challenge_nonce", Fields = new[] { "nonce" }, Unique = true)]
    [Index("wallet_auth_challenge_by_address", Fields = new[] { "address", "chain_type" })]
    public partial class WalletAuthChallenge : ISurrealRecord
    {
        public const string SchemaNameConst = "wallet_auth_challenge";
        public string SchemaName => SchemaNameConst;

        [Id]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("nonce")]
        [Required(NotEmpty = true)]
        public string Nonce { get; set; } = string.Empty;

        [JsonPropertyName("address")]
        [Required(NotEmpty = true)]
        public string Address { get; set; } = string.Empty;

        [JsonPropertyName("chain_type")]
        [Required(NotEmpty = true)]
        public string ChainType { get; set; } = string.Empty;

        [JsonPropertyName("domain_message")]
        [Required(NotEmpty = true)]
        public string DomainMessage { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("consumed_at")]
        public DateTimeOffset? ConsumedAt { get; set; }

        [JsonPropertyName("created_at")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
