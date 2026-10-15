namespace AZOA.WebAPI.Models;

public class HolonCreateModel
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? ParentHolonId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ChainId { get; set; }
    public string? AssetType { get; set; }
    public string? TokenId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public List<Guid> PeerHolonIds { get; set; } = new();

    /// <summary>
    /// Publishes the holon to every caller on the node (HolonManager.CanRead is
    /// owner-or-public). Accepted ONLY for the asset types on
    /// <c>HolonManager.PublishableAssetTypes</c>; anything else is refused rather
    /// than silently ignored. See Services/Governance/AGENTS.md #consumer-governance.
    /// </summary>
    public bool IsPublic { get; set; }
}
