namespace AZOA.WebAPI.Models.Requests;

public class HolonCloneRequest
{
    /// <summary>
    /// Name for the cloned holon. Defaults to original name + " (Copy)".
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// If true, clone the entire subtree (all descendants become children of the clone).
    /// </summary>
    public bool IncludeSubtree { get; set; } = false;

    /// <summary>
    /// New parent for the cloned holon. Null = same parent as original.
    /// </summary>
    public Guid? NewParentId { get; set; }
}
