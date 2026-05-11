namespace OASIS.WebAPI.Models.Quest;

/// <summary>
/// A single executable DAG representing a workflow unit.
/// Each Quest is a directed acyclic graph with entry/terminal nodes,
/// optional dependencies on completed quests, and reusable node templates.
/// </summary>
public class Quest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid AvatarId { get; set; }
    public QuestStatus Status { get; set; }

    public List<QuestNode> Nodes { get; set; } = new();
    public List<QuestEdge> Edges { get; set; } = new();
    public List<QuestDependency> Dependencies { get; set; } = new();

    public Guid? TemplateId { get; set; }
    public Guid? DappSeriesId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedDate { get; set; }
}
