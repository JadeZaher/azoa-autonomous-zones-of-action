using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for <see cref="ActionNodeExecution"/> — the per-(run,
/// node) runtime record introduced by the action-temporal-fork-model track.
/// Replaces in-place mutation of <see cref="ActionNode"/>.State/Output/Error.
/// See <c>conductor/tracks/action-temporal-fork-model/ADR.md</c>.
/// </summary>
/// <remarks>
/// The natural key is <c>(RunId, NodeId)</c>. <see cref="TryClaimPendingAsync"/>
/// is the api-safety-hardening G2 conditional-update primitive (maps to the
/// SurrealDB <c>UPDATE … WHERE state = 'Pending' RETURN AFTER</c> pattern).
/// </remarks>
public interface IActionNodeExecutionStore
{
    /// <summary>Atomically persists frozen input/correlation while claiming one Pending execution at its expected version.</summary>
    Task<AZOAResult<ActionNodeExecution?>> TryAdmitAsync(ActionNodeExecution execution, CancellationToken ct = default, long? expectedRunVersion = null);
    /// <summary>Inserts a new per-(run, node) execution row.</summary>
    Task<AZOAResult<ActionNodeExecution>> CreateAsync(ActionNodeExecution execution, CancellationToken ct = default);

    /// <summary>Loads an execution by its own id.</summary>
    Task<AZOAResult<ActionNodeExecution>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Updates mutable outcome fields at the expected version and optional prior state, preserving admitted intent.</summary>
    Task<AZOAResult<ActionNodeExecution>> UpdateAsync(
        ActionNodeExecution execution,
        ActionNodeState? expectedState = null,
        CancellationToken ct = default);

    /// <summary>All executions for a single run, ordered by <see cref="ActionNodeExecution.StartedAt"/>.</summary>
    Task<AZOAResult<IEnumerable<ActionNodeExecution>>> GetByRunIdAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Exact-match lookup by the natural key <c>(runId, nodeId)</c>.
    /// <c>IsError</c> when no row exists.
    /// </summary>
    Task<AZOAResult<ActionNodeExecution>> GetByRunAndNodeAsync(Guid runId, Guid nodeId, CancellationToken ct = default);

    /// <summary>
    /// G2 claim primitive: conditional update that only succeeds when current
    /// <see cref="ActionNodeExecution.State"/> equals
    /// <see cref="ActionNodeState.Pending"/>. Transitions to
    /// <see cref="ActionNodeState.Running"/> and stamps a fresh
    /// <see cref="ActionNodeExecution.StartedAt"/>.
    /// </summary>
    /// <returns>
    /// The claimed execution row on success. <c>Result == null</c> with
    /// <c>IsError == false</c> when the row exists but is not Pending (lost
    /// race — another worker already claimed it). <c>IsError == true</c>
    /// when the row does not exist at all.
    /// </returns>
    Task<AZOAResult<ActionNodeExecution?>> TryClaimPendingAsync(Guid runId, Guid nodeId, CancellationToken ct = default);
}
