namespace AZOA.WebAPI.Models;

public class HolonBindingInfo : Interfaces.IHolonBindingInfo
{
    public Guid HolonId { get; set; }
    public string HolonName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? PermissionLevel { get; set; }
    public Dictionary<string, string> Permissions { get; set; } = new();
    public bool IsActive { get; set; }
}

public class WalletBindingInfo : Interfaces.IWalletBindingInfo
{
    public Guid WalletId { get; set; }
    public string WalletAddress { get; set; } = string.Empty;
    public string ChainType { get; set; } = string.Empty;
    public string BindingType { get; set; } = string.Empty;
    public string? AccessLevel { get; set; }
    public Dictionary<string, string> AccessPermissions { get; set; } = new();
    public bool IsActive { get; set; }
}
