namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// Aggregated view of a <see cref="QuestRun"/> plus its per-node
/// <see cref="QuestNodeExecution"/> rows, used by the read-only
/// <c>GET /api/quest/runs/{runId}/execution-state</c> endpoint.
/// </summary>
/// <remarks>
/// All runtime state lives on <see cref="QuestRun"/> and
/// <see cref="QuestNodeExecution"/> per ADR §2.2; this POCO is a read
/// projection and never persisted. Counts are derived from
/// <see cref="NodeExecutions"/> on every read so they cannot drift from the
/// underlying rows.
/// </remarks>
public class QuestExecutionState
{
    public Guid RunId { get; set; }
    public Guid QuestId { get; set; }
    public QuestRunStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int TotalNodes { get; set; }
    public int CompletedNodes { get; set; }
    public int FailedNodes { get; set; }
    public int PendingNodes { get; set; }
    public IEnumerable<QuestNodeExecution> NodeExecutions { get; set; } = new List<QuestNodeExecution>();
}
