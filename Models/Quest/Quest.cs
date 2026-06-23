namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// A single executable DAG representing a workflow unit.
/// Each Quest is a directed acyclic graph with entry/terminal nodes,
/// optional dependencies on completed quests, and reusable node templates.
/// </summary>
/// <remarks>
/// Runtime state (status, completion timestamp, per-node outputs) lives on
/// <see cref="QuestRun"/> and <see cref="QuestNodeExecution"/> after the
/// <c>quest-temporal-fork-model</c> track. The Quest definition itself is
/// shape-only and immutable in-flight.
/// </remarks>
public class Quest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid AvatarId { get; set; }

    public List<QuestNode> Nodes { get; set; } = new();
    public List<QuestEdge> Edges { get; set; } = new();
    public List<QuestDependency> Dependencies { get; set; } = new();

    public Guid? TemplateId { get; set; }
    public Guid? DappSeriesId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Definition birthdate. STAYS on the definition (not a runtime artifact).</summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
