using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Validates DAG structure and invariants for a Quest.
/// </summary>
public interface IQuestDagValidator
{
    /// <summary>
    /// Validates the DAG. When <paramref name="fanOutAsError"/> is true a node
    /// with more than one outgoing Control edge is an error (durable engine /
    /// publish gate). When false (default / legacy executor) it is a warning only.
    /// </summary>
    DagValidationResult Validate(Quest quest, bool fanOutAsError = false);
}

/// <summary>
/// Result of a DAG validation check.
/// </summary>
public class DagValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>
    /// Non-fatal advisory notes (e.g. fan-out on the legacy executor path).
    /// Warnings do not set IsValid=false; callers that treat them as errors
    /// (publish gate, durable engine) check this list explicitly.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
    public List<Guid> TopologicalOrder { get; set; } = new();
}
