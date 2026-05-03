namespace OASIS.WebAPI.Interfaces;

public interface IAvatarNFTCompositeResult
{
    Guid AvatarNFTId { get; set; }
    Guid AvatarId { get; set; }
    string NFTContractAddress { get; set; }
    string TokenId { get; set; }
    string ChainType { get; set; }
    string Name { get; set; }
    string? Description { get; set; }
    string? ImageURI { get; set; }
    Dictionary<string, string> Attributes { get; set; }
    List<HolonBindingInfo> HolonBindings { get; set; }
    List<WalletBindingInfo> WalletBindings { get; set; }
    string? CurrentOwner { get; set; }
    bool IsSoulbound { get; set; }
    bool IsTransferable { get; set; }
    bool IsActive { get; set; }
    DateTime MintedDate { get; set; }
    DateTime? LastTransferDate { get; set; }
}

public interface IHolonBindingInfo
{
    Guid HolonId { get; set; }
    string HolonName { get; set; }
    string Role { get; set; }
    string? PermissionLevel { get; set; }
    Dictionary<string, string> Permissions { get; set; }
    bool IsActive { get; set; }
}

public interface IWalletBindingInfo
{
    Guid WalletId { get; set; }
    string WalletAddress { get; set; }
    string ChainType { get; set; }
    string BindingType { get; set; }
    string? AccessLevel { get; set; }
    Dictionary<string, string> AccessPermissions { get; set; }
    bool IsActive { get; set; }
}