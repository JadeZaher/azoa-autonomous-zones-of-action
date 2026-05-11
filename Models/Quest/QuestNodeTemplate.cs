namespace OASIS.WebAPI.Models.Quest;

/// <summary>
/// A reusable node definition that can be instantiated across multiple quests.
/// "Node 2 in iter1 becomes node 1 in iter2."
/// </summary>
public class QuestNodeTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public QuestNodeType NodeType { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Default configuration payload (JSON).
    /// </summary>
    public string DefaultConfig { get; set; } = "{}";

    /// <summary>
    /// JSON Schema for config validation.
    /// </summary>
    public string ConfigSchema { get; set; } = "{}";

    /// <summary>
    /// JSON Schema for expected inputs from upstream nodes.
    /// </summary>
    public string InputSchema { get; set; } = "{}";

    /// <summary>
    /// JSON Schema for produced outputs.
    /// </summary>
    public string OutputSchema { get; set; } = "{}";

    public string Version { get; set; } = "1.0.0";
    public Guid AuthorAvatarId { get; set; }
    public bool IsPublic { get; set; }
    public List<string> Tags { get; set; } = new();
}
