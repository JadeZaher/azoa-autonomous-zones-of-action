namespace AZOA.WebAPI.Interfaces;

public interface ISTARODK
{
    Guid Id { get; set; }
    string Name { get; set; }
    string Description { get; set; }
    string? PublicKey { get; set; }
    string? PrivateKeyHash { get; set; }
    Guid? AvatarId { get; set; }
    List<Guid> BoundHolonIds { get; set; }
    string? TargetChain { get; set; }
    string? GeneratedCode { get; set; }
    string? DeploymentConfig { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? ModifiedDate { get; set; }
    bool IsActive { get; set; }
    /// <summary>Owner opt-in: makes this STAR ODK dApp definition readable cross-avatar as a marketplace template.</summary>
    bool IsPublic { get; set; }
}
