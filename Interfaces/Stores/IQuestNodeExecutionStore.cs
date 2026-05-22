using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for <see cref="QuestNodeExecution"/> — the per-(run,
/// node) runtime record introduced by the quest-temporal-fork-model track.
/// Replaces in-place mutation of <see cref="QuestNode"/>.State/Output/Error.
/// See <c>conductor/tracks/quest-temporal-fork-model/ADR.md</c>.
/// </summary>
/// <remarks>
/// The natural key is <c>(RunId, NodeId)</c>. <see cref="TryClaimPendingAsync"/>
/// is the api-safety-hardening G2 conditional-update primitive (maps to the
/// SurrealDB <c>UPDATE … WHERE state = 'Pending' RETURN AFTER</c> pattern).
/// </remarks>
public interface IQuestNodeExecutionStore
{
    /// <summary>Inserts a new per-(run, node) execution row.</summary>
    Task<OASISResult<QuestNodeExecution>> CreateAsync(QuestNodeExecution execution, CancellationToken ct = default);

    /// <summary>Loads an execution by its own id.</summary>
    Task<OASISResult<QuestNodeExecution>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing execution (state transition, output/error capture, ended_at).
    ///
    /// <para>
    /// When <paramref name="expectedState"/> is non-null the update only
    /// succeeds if the currently-stored execution's
    /// <see cref="QuestNodeExecution.State"/> equals the supplied value —
    /// otherwise an error <see cref="OASISResult{T}"/> is returned describing
    /// the drift. This is the G2 state-machine guard used by
    /// <c>QuestManager</c>'s execute and fork-cancel paths to prevent a
    /// late-arriving in-flight transition from overwriting a concurrent
    /// fork's <see cref="QuestNodeState.Cancelled"/> stamp (and vice-versa).
    /// Closes HIGH#7.
    /// </para>
    /// <para>
    /// When <paramref name="expectedState"/> is <c>null</c> the update is
    /// unconditional, preserving the historic behaviour for callers that
    /// genuinely don't care about the prior state.
    /// </para>
    /// </summary>
    Task<OASISResult<QuestNodeExecution>> UpdateAsync(
        QuestNodeExecution execution,
        QuestNodeState? expectedState = null,
        CancellationToken ct = default);

    /// <summary>All executions for a single run, ordered by <see cref="QuestNodeExecution.StartedAt"/>.</summary>
    Task<OASISResult<IEnumerable<QuestNodeExecution>>> GetByRunIdAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Exact-match lookup by the natural key <c>(runId, nodeId)</c>.
    /// <c>IsError</c> when no row exists.
    /// </summary>
    Task<OASISResult<QuestNodeExecution>> GetByRunAndNodeAsync(Guid runId, Guid nodeId, CancellationToken ct = default);

    /// <summary>
    /// G2 claim primitive: conditional update that only succeeds when current
    /// <see cref="QuestNodeExecution.State"/> equals
    /// <see cref="QuestNodeState.Pending"/>. Transitions to
    /// <see cref="QuestNodeState.Running"/> and stamps a fresh
    /// <see cref="QuestNodeExecution.StartedAt"/>.
    /// </summary>
    /// <returns>
    /// The claimed execution row on success. <c>Result == null</c> with
    /// <c>IsError == false</c> when the row exists but is not Pending (lost
    /// race — another worker already claimed it). <c>IsError == true</c>
    /// when the row does not exist at all.
    /// </returns>
    Task<OASISResult<QuestNodeExecution?>> TryClaimPendingAsync(Guid runId, Guid nodeId, CancellationToken ct = default);
}
