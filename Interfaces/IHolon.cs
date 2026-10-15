namespace AZOA.WebAPI.Interfaces;

public interface IHolon
{
    Guid Id { get; set; }
    string Name { get; set; }
    string Description { get; set; }
    Guid? ParentHolonId { get; set; }
    Guid? UserId { get; set; }
    string ProviderName { get; set; }
    string? ChainId { get; set; }
    string? AssetType { get; set; }
    string? TokenId { get; set; }
    Dictionary<string, string> Metadata { get; set; }
    List<Guid> PeerHolonIds { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? ModifiedDate { get; set; }
    bool IsActive { get; set; }
    Guid? SourceHolonId { get; set; }
    Guid? OriginUserId { get; set; }
    /// <summary>Owner opt-in: makes this holon readable/clonable cross-user as a marketplace template.</summary>
    bool IsPublic { get; set; }
}
