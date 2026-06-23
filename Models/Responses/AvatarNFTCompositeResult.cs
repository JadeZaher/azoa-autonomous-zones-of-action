using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models.Responses;

public class AvatarNFTCompositeResult : IAvatarNFTCompositeResult
{
    public Guid AvatarNFTId { get; set; }
    public Guid AvatarId { get; set; }
    public string NFTContractAddress { get; set; } = string.Empty;
    public string TokenId { get; set; } = string.Empty;
    public string ChainType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageURI { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
    public List<HolonBindingInfo> HolonBindings { get; set; } = new();
    public List<WalletBindingInfo> WalletBindings { get; set; } = new();
    public string? CurrentOwner { get; set; }
    public bool IsSoulbound { get; set; }
    public bool IsTransferable { get; set; }
    public bool IsActive { get; set; }
    public DateTime MintedDate { get; set; }
    public DateTime? LastTransferDate { get; set; }
}