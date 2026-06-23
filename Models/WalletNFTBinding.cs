using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models;

public class WalletNFTBinding : IWalletNFTBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WalletId { get; set; }
    public Guid AvatarNFTId { get; set; }
    public string BindingType { get; set; } = string.Empty; // "primary", "secondary", "authorized"
    public string? AccessLevel { get; set; } // "full", "transaction", "view"
    public Dictionary<string, string> AccessPermissions { get; set; } = new();
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? LastUpdatedDate { get; set; }
    public bool IsActive { get; set; } = true;
    
    // Navigation properties
    public Wallet? Wallet { get; set; }
    public AvatarNFT? AvatarNFT { get; set; }
}