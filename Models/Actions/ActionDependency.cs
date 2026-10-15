namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// A cross-action dependency — this action depends on the completion
/// (or specific node output) of a prior action.
/// </summary>
public class ActionDependency
{
    public Guid Id { get; set; }
    public Guid ActionId { get; set; }
    public Guid DependsOnActionId { get; set; }

    /// <summary>
    /// Optional: depend on a specific node output rather than full action completion.
    /// </summary>
    public Guid? DependsOnNodeId { get; set; }

    public ActionDependencyType DependencyType { get; set; }
}
