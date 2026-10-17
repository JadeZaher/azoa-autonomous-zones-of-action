namespace AZOA.WebAPI.Models.Requests;

public class UserNFTMintModel
{
    public Guid? WalletId { get; set; }
    public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? FeeAuthorization { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AZOA.WebAPI.Models.Blockchain.NftProtocolCostAuthorization? ProtocolCostAuthorization { get; set; }
    public string ChainType { get; set; } = string.Empty;
    /// <summary>Must be absent for creation; imports cannot establish issuance provenance.</summary>
    public string? TokenId { get; set; }
    public string NFTContractAddress { get; set; } = string.Empty;
    public string TokenStandard { get; set; } = "ERC721";
    public string MetadataURI { get; set; } = string.Empty;
    public string? ImageURI { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
    public decimal RoyaltyPercentage { get; set; }
    public string? RoyaltyRecipient { get; set; }
    public bool IsSoulbound { get; set; }
    public bool IsTransferable { get; set; } = true;
    public Dictionary<string, string>? HolonBindings { get; set; }
    public Dictionary<string, string>? WalletBindings { get; set; }
}
