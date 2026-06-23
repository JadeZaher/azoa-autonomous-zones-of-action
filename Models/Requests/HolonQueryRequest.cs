namespace AZOA.WebAPI.Models.Requests;

public class HolonQueryRequest
{
    public string? Name { get; set; }
    public Guid? AvatarId { get; set; }
    public string? ProviderName { get; set; }
    public string? ChainId { get; set; }
    public string? AssetType { get; set; }
    public bool? IsActive { get; set; }
    public Guid? ParentHolonId { get; set; }
}
