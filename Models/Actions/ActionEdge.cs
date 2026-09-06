namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// A directed control-flow dependency between two nodes in the same action.
/// </summary>
public class ActionEdge
{
    public Guid Id { get; set; }
    public Guid ActionId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }

    /// <summary>
    /// Optional condition expression for edge activation (used with Conditional edge type).
    /// </summary>
    public string? Condition { get; set; }

    public ActionEdgeType EdgeType { get; set; } = ActionEdgeType.Control;
}
