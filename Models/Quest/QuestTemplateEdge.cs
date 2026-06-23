namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// An edge within a QuestTemplate.
/// </summary>
public class QuestTemplateEdge
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string SourceSlotId { get; set; } = string.Empty;
    public string TargetSlotId { get; set; } = string.Empty;
    public QuestEdgeType EdgeType { get; set; } = QuestEdgeType.Control;
}
