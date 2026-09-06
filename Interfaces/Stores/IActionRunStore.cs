using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for <see cref="ActionRun"/> — the per-attempt runtime
/// counterpart to the immutable <see cref="ActionDefinition"/> definition. Introduced by
/// the action-temporal-fork-model track; see
/// <c>conductor/tracks/action-temporal-fork-model/ADR.md</c>.
/// </summary>
/// <remarks>
/// Lineage forms a tree via <see cref="ActionRun.ParentRunId"/>; there is no
/// merge-of-forks (deliberate non-goal). <see cref="GetLineageAsync"/> walks
/// the chain in child-to-root order.
/// </remarks>
public interface IActionRunStore
{
    /// <summary>Inserts a new run row.</summary>
    Task<AZOAResult<ActionRun>> CreateAsync(ActionRun run, CancellationToken ct = default);

    /// <summary>Loads a single run by id; <c>IsError</c> when not found.</summary>
    Task<AZOAResult<ActionRun>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing run (status transition, ended_at, fork fields, etc.).
    /// When <paramref name="expectedStatus"/> is supplied, the write is a G2
    /// single-winner conditional UPDATE — it applies ONLY if the persisted row
    /// is still in that status, mirroring <see cref="IActionNodeExecutionStore"/>'s
    /// <c>expectedState</c> guard. A concurrent projector that already moved the
    /// run loses (zero-row write ⇒ <c>IsError</c>), so two racing status
    /// projections never clobber each other and a terminal verdict is never
    /// regressed by a stale read-modify-write. <c>null</c> ⇒ unconditional
    /// update (back-compat for the existing supervisor/fork paths).
    /// </summary>
    Task<AZOAResult<ActionRun>> UpdateAsync(
        ActionRun run, ActionRunStatus? expectedStatus = null, CancellationToken ct = default);

    /// <summary>All runs for a single <see cref="ActionDefinition"/> definition.</summary>
    Task<AZOAResult<IEnumerable<ActionRun>>> GetByActionIdAsync(Guid actionId, CancellationToken ct = default);

    /// <summary>All runs initiated by an user across any action.</summary>
    Task<AZOAResult<IEnumerable<ActionRun>>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>All runs currently in a given status (e.g. <see cref="ActionRunStatus.Running"/>).</summary>
    Task<AZOAResult<IEnumerable<ActionRun>>> GetByStatusAsync(ActionRunStatus status, CancellationToken ct = default);

    /// <summary>
    /// Returns the ancestor chain of a run, starting with the run itself and
    /// walking <see cref="ActionRun.ParentRunId"/> until null. Child-to-root
    /// order. <c>IsError</c> if <paramref name="runId"/> does not exist.
    /// </summary>
    Task<AZOAResult<IEnumerable<ActionRun>>> GetLineageAsync(Guid runId, CancellationToken ct = default);
}
