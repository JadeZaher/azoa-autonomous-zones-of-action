namespace OASIS.WebAPI.Models;

public class HolonUpdateModel
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Guid? ParentHolonId { get; set; }
    public string? ProviderName { get; set; }
    public string? ChainId { get; set; }
    public string? AssetType { get; set; }
    public string? TokenId { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public List<Guid>? PeerHolonIds { get; set; }
    public bool? IsActive { get; set; }
}
