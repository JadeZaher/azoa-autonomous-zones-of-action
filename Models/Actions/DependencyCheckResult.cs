namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// Result of evaluating an ActionDefinition's cross-action <see cref="ActionDependency"/>
/// list. A dependency is considered satisfied when the referenced action has
/// at least one <see cref="ActionRun"/> in <see cref="ActionRunStatus.Succeeded"/>.
/// </summary>
/// <remarks>
/// Returned by the read-only check endpoint
/// (<c>GET /api/action/{actionId}/dependency-status</c>) so callers can decide
/// whether to invoke <c>ExecuteAsync</c>. The check does not block execution
/// at the manager layer — it is informational. <see cref="UnsatisfiedDependencyIds"/>
/// carries the <see cref="ActionDependency.Id"/> values (not the dependent action
/// ids) so callers can correlate back to the dependency rows directly.
/// </remarks>
public class DependencyCheckResult
{
    public bool AllSatisfied { get; set; }
    public IEnumerable<Guid> UnsatisfiedDependencyIds { get; set; } = new List<Guid>();
    public string? Message { get; set; }
}
