namespace OASIS.WebAPI.Models.Quest;

/// <summary>
/// A cross-quest dependency — this quest depends on the completion
/// (or specific node output) of a prior quest.
/// </summary>
public class QuestDependency
{
    public Guid Id { get; set; }
    public Guid QuestId { get; set; }
    public Guid DependsOnQuestId { get; set; }

    /// <summary>
    /// Optional: depend on a specific node output rather than full quest completion.
    /// </summary>
    public Guid? DependsOnNodeId { get; set; }

    public QuestDependencyType DependencyType { get; set; }
}
