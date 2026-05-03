namespace OASIS.WebAPI.Interfaces;

public interface IHolon
{
    Guid Id { get; set; }
    string Name { get; set; }
    string Description { get; set; }
    Guid? ParentHolonId { get; set; }
    Guid? AvatarId { get; set; }
    string ProviderName { get; set; }
    string? ChainId { get; set; }
    string? AssetType { get; set; }
    string? TokenId { get; set; }
    Dictionary<string, string> Metadata { get; set; }
    List<Guid> PeerHolonIds { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? ModifiedDate { get; set; }
    bool IsActive { get; set; }
}
