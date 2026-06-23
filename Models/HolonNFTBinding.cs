using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models;

public class HolonNFTBinding : IHolonNFTBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HolonId { get; set; }
    public Guid AvatarNFTId { get; set; }
    public string Role { get; set; } = string.Empty; // "owner", "operator", "delegate", etc.
    public string? PermissionLevel { get; set; } // "full", "limited", "read-only"
    public Dictionary<string, string> Permissions { get; set; } = new();
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? LastUpdatedDate { get; set; }
    public bool IsActive { get; set; } = true;
    
    // Navigation properties
    public Holon? Holon { get; set; }
    public AvatarNFT? AvatarNFT { get; set; }
}