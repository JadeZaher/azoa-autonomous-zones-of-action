using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models;

public class AvatarNFT : IAvatarNFT
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AvatarId { get; set; }
    public string NFTContractAddress { get; set; } = string.Empty;
    public string TokenId { get; set; } = string.Empty;
    public string ChainType { get; set; } = string.Empty;
    public string TokenStandard { get; set; } = string.Empty; // ERC721, ERC1155, etc.
    public string MetadataURI { get; set; } = string.Empty;
    public string? ImageURI { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
    public decimal RoyaltyPercentage { get; set; }
    public string? RoyaltyRecipient { get; set; }
    public bool IsSoulbound { get; set; }
    public bool IsTransferable { get; set; } = true;
    public DateTime MintedDate { get; set; } = DateTime.UtcNow;
    public DateTime? LastTransferDate { get; set; }
    public string? CurrentOwner { get; set; }
    public bool IsActive { get; set; } = true;
    
    // Navigation properties
    public Avatar? Avatar { get; set; }
    public List<HolonNFTBinding> HolonBindings { get; set; } = new();
    public List<WalletNFTBinding> WalletBindings { get; set; } = new();
}