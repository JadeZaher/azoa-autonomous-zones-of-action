namespace AZOA.WebAPI.Interfaces;

public interface IActionGraphBundle
{
    Guid Id { get; set; }
    string Name { get; set; }
    string Description { get; set; }
    string? PublicKey { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    string? PrivateKeyHash { get; set; }
    Guid? UserId { get; set; }
    List<Guid> BoundHolonIds { get; set; }
    string? TargetChain { get; set; }
    string? GeneratedCode { get; set; }
    string? DeploymentConfig { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? ModifiedDate { get; set; }
    bool IsActive { get; set; }
    /// <summary>Owner opt-in: makes this action graph bundle dApp definition readable cross-user as a marketplace template.</summary>
    bool IsPublic { get; set; }
}
