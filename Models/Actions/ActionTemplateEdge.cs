namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// An edge within an ActionTemplate.
/// </summary>
public class ActionTemplateEdge
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string SourceSlotId { get; set; } = string.Empty;
    public string TargetSlotId { get; set; } = string.Empty;
    public ActionEdgeType EdgeType { get; set; } = ActionEdgeType.Control;
}
