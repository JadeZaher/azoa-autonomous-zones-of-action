namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// A super-holon aggregate view computed from children — no new storage, pure composition.
/// </summary>
public class HolonComposition
{
    public Guid SourceHolonId { get; set; }
    public string SourceHolonName { get; set; } = string.Empty;
    public int ChildCount { get; set; }
    public int TotalDescendantCount { get; set; }
    public int Depth { get; set; }
    public List<string> AssetTypes { get; set; } = new();
    public List<string> ChainIds { get; set; } = new();
    public Dictionary<string, int> MetadataKeyFrequency { get; set; } = new();
    public bool AllActive { get; set; }
    public DateTime? EarliestCreated { get; set; }
    public DateTime? LatestModified { get; set; }
}
