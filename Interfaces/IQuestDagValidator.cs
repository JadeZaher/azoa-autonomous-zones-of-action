using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Validates DAG structure and invariants for a Quest.
/// </summary>
public interface IQuestDagValidator
{
    DagValidationResult Validate(Quest quest);
}

/// <summary>
/// Result of a DAG validation check.
/// </summary>
public class DagValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<Guid> TopologicalOrder { get; set; } = new();
}
