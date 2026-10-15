namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// A reusable action definition — a full DAG template
/// that can be instantiated with parameters.
/// </summary>
public class ActionTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid AuthorUserId { get; set; }

    public List<ActionTemplateNode> Nodes { get; set; } = new();
    public List<ActionTemplateEdge> Edges { get; set; } = new();

    /// <summary>
    /// JSON Schema for parameters required for instantiation.
    /// </summary>
    public string Parameters { get; set; } = "{}";

    public string Version { get; set; } = "1.0.0";
    public bool IsPublic { get; set; }
    public List<string> Tags { get; set; } = new();
}
