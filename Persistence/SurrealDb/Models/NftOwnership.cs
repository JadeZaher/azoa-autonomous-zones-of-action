// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the nft_ownership table.

#nullable enable

using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("nft_ownership",
        Aggregate = "UserNFT (Models/UserNFT.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("is_current distinguishes live ownership from historical transfer rows. Adapter enforces the (chain, contract, token_id, is_current=true) singleton invariant.")]
    [SurrealNote("issuer_user_id (D19) is the authorization anchor for credential revocation: only the ISSUER may claw a soulbound credential back. It is nullable because every row minted before D19 has no recorded issuer, and a revoke of such a row must fail closed rather than infer one from current_owner (that is the holder) or from an operation log (a tenant-proxied mint stamps the wrong party).")]
    [SurrealNote("B3 review: nft_chain_contract_token_current is intentionally NOT UNIQUE because is_current=false rows accumulate as transfer history; uniqueness of the (chain, contract, token_id, is_current=true) tuple is enforced by the adapter setting is_current=false on the prior row before inserting the new one (SurrealDB does not support partial UNIQUE INDEXes).")]
    [Slice("wallet_nft")]
    [Index("nft_chain_contract_token_current", Fields = new[] { "chain_type", "contract_address", "token_id", "is_current" })]
    [Index("nft_user_id", Fields = new[] { "user_id" })]
    [Index("nft_chain_contract", Fields = new[] { "chain_type", "contract_address" })]
    public partial class NftOwnership : ISurrealRecord
    {
        public const string SchemaNameConst = "nft_ownership";
        public string SchemaName => SchemaNameConst;

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [References(typeof(User))]
        public string UserId { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string ChainType { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string ContractAddress { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string TokenId { get; set; } = string.Empty;

        [FieldGroup("Token standard (ERC721, ERC1155, ARC3, ...)")]
        [Required(NotEmpty = true)]
        public string TokenStandard { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string MetadataUri { get; set; } = string.Empty;

        public string? ImageUri { get; set; }

        public string? Name { get; set; }

        public string? Description { get; set; }

        [Column(Flexible = true)]
        [FieldGroup("Attributes (flexible key->value bag)")]
        public JsonElement? Attributes { get; set; }

        [Default("0.0")]
        public decimal RoyaltyPercentage { get; set; }

        public string? RoyaltyRecipient { get; set; }

        [Default("false")]
        public bool IsSoulbound { get; set; }

        [Default("true")]
        public bool IsTransferable { get; set; }

        [FieldGroup("is_current: true = live ownership; false = historical")]
        [Default("true")]
        public bool IsCurrent { get; set; }

        public string? CurrentOwner { get; set; }

        [FieldGroup("Credential issuer (D19) — the party that MINTED this asset, NOT current_owner")]
        [References(typeof(User), Optional = true)]
        public string? IssuerUserId { get; set; }

        [Default("true")]
        public bool IsActive { get; set; }

        [ReadOnly]
        public DateTimeOffset MintedDate { get; set; }

        public DateTimeOffset? LastTransferDate { get; set; }
    }
}
