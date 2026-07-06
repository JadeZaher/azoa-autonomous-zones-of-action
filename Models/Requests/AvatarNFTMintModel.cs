namespace AZOA.WebAPI.Models.Requests;

public class AvatarNFTMintModel
{
    public string ChainType { get; set; } = string.Empty;
    /// Optional caller-supplied token id; when empty the mint assigns one (chain-mint semantics).
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