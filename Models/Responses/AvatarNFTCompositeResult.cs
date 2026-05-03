using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Models.Responses;

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
    
    // Holon Bindings
    public List<HolonBindingInfo> HolonBindings { get; set; } = new();
    
    // Wallet Bindings
    public List<WalletBindingInfo> WalletBindings { get; set; } = new();
    
    // Ownership and Status
    public string? CurrentOwner { get; set; }
    public bool IsSoulbound { get; set; }
    public bool IsTransferable { get; set; }
    public bool IsActive { get; set; }
    public DateTime MintedDate { get; set; }
    public DateTime? LastTransferDate { get; set; }
}

public class HolonBindingInfo
{
    public Guid HolonId { get; set; }
    public string HolonName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? PermissionLevel { get; set; }
    public Dictionary<string, string> Permissions { get; set; } = new();
    public bool IsActive { get; set; }
}

public class WalletBindingInfo
{
    public Guid WalletId { get; set; }
    public string WalletAddress { get; set; } = string.Empty;
    public string ChainType { get; set; } = string.Empty;
    public string BindingType { get; set; } = string.Empty;
    public string? AccessLevel { get; set; }
    public Dictionary<string, string> AccessPermissions { get; set; } = new();
    public bool IsActive { get; set; }
}