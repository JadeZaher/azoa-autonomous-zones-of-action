using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>One current ownership authority per confirmed chain/network/asset identity.</summary>
[SurrealTable("nft_asset_authority", Aggregate = "NFT authority", Guardrail = "G6 SCHEMAFULL")]
[Slice("wallet_nft")]
[Index("nft_authority_nft", Fields = new[] { "nft_id" }, Unique = true)]
[Index("nft_authority_asset", Fields = new[] { "chain_type", "network", "protocol_identity", "contract_address", "asset_id" }, Unique = true)]
public partial class NftAssetAuthority : ISurrealRecord
{
    public const string SchemaNameConst = "nft_asset_authority";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string NftId { get; set; } = string.Empty;
    public string ChainType { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string ProtocolIdentity { get; set; } = string.Empty;
    public string ContractAddress { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string TokenStandard { get; set; } = string.Empty;
    public string IssuerUserId { get; set; } = string.Empty;
    public string CreatorUserId { get; set; } = string.Empty;
    public string? ActiveLifecycleId { get; set; }
    public string HolderUserId { get; set; } = string.Empty;
    public string OwnerAddress { get; set; } = string.Empty;
    public string CreationTransactionId { get; set; } = string.Empty;
    public string IssuanceLifecycleId { get; set; } = string.Empty;
    public string LatestTransactionId { get; set; } = string.Empty;
    public string LatestLifecycleId { get; set; } = string.Empty;
    public bool Soulbound { get; set; }
    public bool Burned { get; set; }
    public bool Revoked { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
