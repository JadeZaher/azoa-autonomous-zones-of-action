namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// Aggregated view of a <see cref="ActionRun"/> plus its per-node
/// <see cref="ActionNodeExecution"/> rows, used by the read-only
/// <c>GET /api/action/runs/{runId}/execution-state</c> endpoint.
/// </summary>
/// <remarks>
/// All runtime state lives on <see cref="ActionRun"/> and
/// <see cref="ActionNodeExecution"/> per ADR §2.2; this POCO is a read
/// projection and never persisted. Counts are derived from
/// <see cref="NodeExecutions"/> on every read so they cannot drift from the
/// underlying rows.
/// </remarks>
public class ActionExecutionState
{
    public Guid RunId { get; set; }
    public Guid ActionId { get; set; }
    public ActionRunStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int TotalNodes { get; set; }
    public int CompletedNodes { get; set; }
    public int FailedNodes { get; set; }
    public int PendingNodes { get; set; }
    public IEnumerable<ActionNodeExecution> NodeExecutions { get; set; } = new List<ActionNodeExecution>();
}
