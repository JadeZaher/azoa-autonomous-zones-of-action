namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// A single task/step within a quest DAG.
/// Wraps a call to an existing AZOA manager method.
/// </summary>
/// <remarks>
/// Runtime per-node state (status, output, error) lives on
/// <see cref="QuestNodeExecution"/> keyed by <c>(RunId, NodeId)</c> after the
/// <c>quest-temporal-fork-model</c> track. The node definition is shape-only.
/// </remarks>
public class QuestNode
{
    public Guid Id { get; set; }
    public Guid QuestId { get; set; }
    public Guid? NodeTemplateId { get; set; }
    public QuestNodeType NodeType { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Node-specific config serialized as JSON.
    /// Deserialized to the matching request model at execution time.
    /// </summary>
    public string Config { get; set; } = "{}";

    /// <summary>
    /// Entry point node (no incoming control edges).
    /// </summary>
    public bool IsEntry { get; set; }

    /// <summary>
    /// Terminal node (no outgoing control edges).
    /// </summary>
    public bool IsTerminal { get; set; }

    /// <summary>
    /// Topological position (computed during validation).
    /// </summary>
    public int ExecutionOrder { get; set; }
}
