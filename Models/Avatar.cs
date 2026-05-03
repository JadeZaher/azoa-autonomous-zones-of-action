using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Models;

public class Avatar : IAvatar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? LastBeamedInDate { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsVerified { get; set; }
    public int Karma { get; set; }
    public int Level { get; set; } = 1;

    public List<Wallet> Wallets { get; set; } = new();
}
