namespace OASIS.WebAPI.Models;

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AvatarId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the actual key. The raw key is only shown once at creation.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// First 8 characters of the key for display/identification (e.g., "oasis_ab12cd34...").
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional: comma-separated list of allowed scopes (e.g., "read,write,admin").
    /// Empty means full access.
    /// </summary>
    public string? Scopes { get; set; }
}
