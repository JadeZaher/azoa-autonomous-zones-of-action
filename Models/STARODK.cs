using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Models;

public class STARODK : ISTARODK
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    public string? PrivateKeyHash { get; set; }
    public Guid? AvatarId { get; set; }
    public List<Guid> BoundHolonIds { get; set; } = new();
    public string? TargetChain { get; set; }
    public string? GeneratedCode { get; set; }
    public string? DeploymentConfig { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }
    public bool IsActive { get; set; } = true;
}
