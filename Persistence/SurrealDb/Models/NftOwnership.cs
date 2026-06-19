// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the nft_ownership table.

#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("nft_ownership",
        Aggregate = "AvatarNFT (Models/AvatarNFT.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("is_current distinguishes live ownership from historical transfer rows. Adapter enforces the (chain, contract, token_id, is_current=true) singleton invariant.")]
    [SurrealNote("B3 review: nft_chain_contract_token_current is intentionally NOT UNIQUE because is_current=false rows accumulate as transfer history; uniqueness of the (chain, contract, token_id, is_current=true) tuple is enforced by the adapter setting is_current=false on the prior row before inserting the new one (SurrealDB does not support partial UNIQUE INDEXes).")]
    [Slice("wallet_nft")]
    [Index("nft_chain_contract_token_current", Fields = new[] { "chain_type", "contract_address", "token_id", "is_current" })]
    [Index("nft_avatar_id", Fields = new[] { "avatar_id" })]
    [Index("nft_chain_contract", Fields = new[] { "chain_type", "contract_address" })]
    public partial class NftOwnership : ISurrealRecord
    {
        public const string SchemaNameConst = "nft_ownership";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [References(typeof(Avatar))]
        [JsonPropertyName("avatar_id")]
        public string AvatarId { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("chain_type")]
        public string ChainType { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("contract_address")]
        public string ContractAddress { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("token_id")]
        public string TokenId { get; set; } = string.Empty;

        [Column(Order = 6, Type = "string")]
        [FieldGroup("Token standard (ERC721, ERC1155, ARC3, ...)")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("token_standard")]
        public string TokenStandard { get; set; } = string.Empty;

        [Column(Order = 7, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("metadata_uri")]
        public string MetadataUri { get; set; } = string.Empty;

        [Column(Order = 8, Type = "option<string>")]
        [JsonPropertyName("image_uri")]
        public string? ImageUri { get; set; }

        [Column(Order = 9, Type = "option<string>")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [Column(Order = 10, Type = "option<string>")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [Column(Order = 11, Type = "option<object>")]
        [FieldGroup("Attributes (flexible key->value bag)")]
        [JsonPropertyName("attributes")]
        public JsonElement? Attributes { get; set; }

        [Column(Order = 12, Type = "decimal")]
        [Default("0.0")]
        [JsonPropertyName("royalty_percentage")]
        public decimal RoyaltyPercentage { get; set; }

        [Column(Order = 13, Type = "option<string>")]
        [JsonPropertyName("royalty_recipient")]
        public string? RoyaltyRecipient { get; set; }

        [Column(Order = 14, Type = "bool")]
        [Default("false")]
        [JsonPropertyName("is_soulbound")]
        public bool IsSoulbound { get; set; }

        [Column(Order = 15, Type = "bool")]
        [Default("true")]
        [JsonPropertyName("is_transferable")]
        public bool IsTransferable { get; set; }

        [Column(Order = 16, Type = "bool")]
        [FieldGroup("is_current: true = live ownership; false = historical")]
        [Default("true")]
        [JsonPropertyName("is_current")]
        public bool IsCurrent { get; set; }

        [Column(Order = 17, Type = "option<string>")]
        [JsonPropertyName("current_owner")]
        public string? CurrentOwner { get; set; }

        [Column(Order = 18, Type = "bool")]
        [Default("true")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [Column(Order = 19, Type = "datetime")]
        [ReadOnly]
        [JsonPropertyName("minted_date")]
        public DateTimeOffset MintedDate { get; set; }

        [Column(Order = 20, Type = "option<datetime>")]
        [JsonPropertyName("last_transfer_date")]
        public DateTimeOffset? LastTransferDate { get; set; }
    }
}
