using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Models;

public class Wallet : IWallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AvatarId { get; set; }
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    public string? Label { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
