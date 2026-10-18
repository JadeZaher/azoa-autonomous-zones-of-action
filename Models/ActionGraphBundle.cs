using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models;

public class ActionGraphBundle : IActionGraphBundle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PrivateKeyHash { get; set; }
    public Guid? UserId { get; set; }
    public List<Guid> BoundHolonIds { get; set; } = new();
    public string? TargetChain { get; set; }
    public string? GeneratedCode { get; set; }
    public string? DeploymentConfig { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPublic { get; set; } = false;
}
