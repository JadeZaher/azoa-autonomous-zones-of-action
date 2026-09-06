using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persistence boundary for <see cref="ActionDefinition"/>, <see cref="ActionTemplate"/> and <see cref="ActionNodeTemplate"/>.</summary>
public interface IActionStore
{
    /// <summary>Loads a single action by id.</summary>
    Task<AZOAResult<ActionDefinition>> GetActionAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads all actions owned by an user.</summary>
    Task<AZOAResult<IEnumerable<ActionDefinition>>> GetActionsByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Loads all actions the owner opted into the public marketplace (IsPublic=true). Status filtered by the caller.</summary>
    Task<AZOAResult<IEnumerable<ActionDefinition>>> GetPublicActionsAsync(CancellationToken ct = default);

    /// <summary>Loads all actions belonging to a dapp series.</summary>
    Task<AZOAResult<IEnumerable<ActionDefinition>>> GetActionsByDappSeriesAsync(Guid dappSeriesId, CancellationToken ct = default);

    /// <summary>Inserts or updates a action (including its node/edge graph).</summary>
    Task<AZOAResult<ActionDefinition>> UpsertActionAsync(ActionDefinition action, CancellationToken ct = default);

    /// <summary>Deletes a action by id.</summary>
    Task<AZOAResult<bool>> DeleteActionAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap the definition lifecycle Status (F6 TOCTOU guard).
    /// Issues a single conditional UPDATE that flips <paramref name="expected"/> →
    /// <paramref name="next"/> and increments <c>version</c> ONLY when the row is
    /// still at (<paramref name="expected"/>, <paramref name="expectedVersion"/>).
    /// Returns the affected-row count VERBATIM (0 = lost the race / stale version;
    /// 1 = won). The store never asserts==1, retries, or read-modify-writes.
    /// Mirrors <c>IBridgeStore.TryTransitionBridgeStatusAsync</c>.
    /// </summary>
    Task<int> TryTransitionActionStatusAsync(Guid id, ActionStatus expected, ActionStatus next, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Confirms the action is STILL at (<paramref name="expected"/>,
    /// <paramref name="expectedVersion"/>) via a conditional no-op self-write,
    /// without changing status or version. Returns the affected-row count VERBATIM
    /// (1 = definition unchanged since the caller read it; 0 = it moved — e.g. an
    /// unpublish raced this run-start). Closes the unpublish-vs-run-start TOCTOU.
    /// </summary>
    Task<int> TryConfirmActionStateAsync(Guid id, ActionStatus expected, long expectedVersion, CancellationToken ct = default);

    /// <summary>Loads a single action template by id.</summary>
    Task<AZOAResult<ActionTemplate>> GetActionTemplateAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads every action template.</summary>
    Task<AZOAResult<IEnumerable<ActionTemplate>>> GetAllActionTemplatesAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates a action template.</summary>
    Task<AZOAResult<ActionTemplate>> UpsertActionTemplateAsync(ActionTemplate template, CancellationToken ct = default);

    /// <summary>Deletes a action template by id.</summary>
    Task<AZOAResult<bool>> DeleteActionTemplateAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts or updates a action node template.</summary>
    Task<AZOAResult<ActionNodeTemplate>> UpsertActionNodeTemplateAsync(ActionNodeTemplate template, CancellationToken ct = default);

    /// <summary>Loads every action node template.</summary>
    Task<AZOAResult<IEnumerable<ActionNodeTemplate>>> GetAllActionNodeTemplatesAsync(CancellationToken ct = default);
}
