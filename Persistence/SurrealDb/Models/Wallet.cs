// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO -- C#-first authoring surface.
//
// Replaces the previous Mermaid-sourced generator output for the `wallet`
// table. Carries both the .surql-emit metadata ([SurrealTable], [Column],
// [Assert], [Index], etc.) AND the runtime JSON shape consumers depend
// on ([JsonPropertyName], ISurrealRecord.SchemaName). Lives in
// AZOA.WebAPI.Generated.SurrealDb to preserve source-compat with the 30+
// files that import that namespace.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("wallet",
        Aggregate = "Wallet (Models/Wallet.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("No balance field -- chain is source of truth (data-engine-decision).")]
    [SurrealNote("B3 review: wallet_avatar_chain_address UNIQUE is on (avatar_id, chain_type, address) -- all three NOT NULL; SurrealDB NULL-collision concern does not apply.")]
    [Slice("wallet_nft")]
    [Index("wallet_avatar_chain_address", Fields = new[] { "avatar_id", "chain_type", "address" }, Unique = true)]
    [Index("wallet_avatar_chain", Fields = new[] { "avatar_id", "chain_type" })]
    public partial class Wallet : ISurrealRecord
    {
        public const string SchemaNameConst = "wallet";
        public string SchemaName => SchemaNameConst;

        public enum WalletTypeKind
        {
            External,
            Platform,
        }

        [Id]
        public string Id { get; set; } = string.Empty;

        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string ChainType { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Address { get; set; } = string.Empty;

        public string? PublicKey { get; set; }

        public string? Label { get; set; }

        [Default("false")]
        public bool IsDefault { get; set; }

        [Inside("External", "Platform")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WalletTypeKind WalletType { get; set; }

        public string? EncryptedPrivateKey { get; set; }

        public string? EncryptedSeedPhrase { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }
    }
}
