namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// A directed control-flow dependency between two nodes in the same quest.
/// </summary>
public class QuestEdge
{
    public Guid Id { get; set; }
    public Guid QuestId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }

    /// <summary>
    /// Optional condition expression for edge activation (used with Conditional edge type).
    /// </summary>
    public string? Condition { get; set; }

    public QuestEdgeType EdgeType { get; set; } = QuestEdgeType.Control;
}
