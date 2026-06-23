namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// Result of evaluating a Quest's cross-quest <see cref="QuestDependency"/>
/// list. A dependency is considered satisfied when the referenced quest has
/// at least one <see cref="QuestRun"/> in <see cref="QuestRunStatus.Succeeded"/>.
/// </summary>
/// <remarks>
/// Returned by the read-only check endpoint
/// (<c>GET /api/quest/{questId}/dependency-status</c>) so callers can decide
/// whether to invoke <c>ExecuteAsync</c>. The check does not block execution
/// at the manager layer — it is informational. <see cref="UnsatisfiedDependencyIds"/>
/// carries the <see cref="QuestDependency.Id"/> values (not the dependent quest
/// ids) so callers can correlate back to the dependency rows directly.
/// </remarks>
public class DependencyCheckResult
{
    public bool AllSatisfied { get; set; }
    public IEnumerable<Guid> UnsatisfiedDependencyIds { get; set; } = new List<Guid>();
    public string? Message { get; set; }
}
