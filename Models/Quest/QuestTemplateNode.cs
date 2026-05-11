namespace OASIS.WebAPI.Models.Quest;

/// <summary>
/// A node within a QuestTemplate (parameterized, not yet instantiated).
/// </summary>
public class QuestTemplateNode
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }

    /// <summary>
    /// Logical slot identifier (e.g. "step_1", "boss_fight").
    /// </summary>
    public string SlotId { get; set; } = string.Empty;

    public Guid NodeTemplateId { get; set; }

    /// <summary>
    /// Template-level param overrides (JSON).
    /// </summary>
    public string ParamOverrides { get; set; } = "{}";

    public bool IsEntry { get; set; }
    public bool IsTerminal { get; set; }
}
