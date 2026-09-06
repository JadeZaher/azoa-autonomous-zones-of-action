using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for <see cref="ActionAccessRequest"/> — a requester
/// user's self-service request to run an InviteOnly <see cref="ActionDefinition"/>.
/// Introduced by the action-invitations-approval track. State machine +
/// idempotency invariant live in
/// <c>Persistence/SurrealDb/Models/AGENTS.md §action-access-request</c>.
/// </summary>
public interface IActionAccessRequestStore
{
    /// <summary>Inserts a new access-request row.</summary>
    Task<AZOAResult<ActionAccessRequest>> CreateAsync(ActionAccessRequest request, CancellationToken ct = default);

    /// <summary>Loads a single request by id; <c>IsError</c> when not found.</summary>
    Task<AZOAResult<ActionAccessRequest>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>All requests for a action, optionally filtered to a single status (owner approval queue).</summary>
    Task<AZOAResult<IEnumerable<ActionAccessRequest>>> GetByActionAsync(
        Guid actionId, ActionAccessRequestStatus? status = null, CancellationToken ct = default);

    /// <summary>All requests raised by a requester across any action, optionally status-filtered.</summary>
    Task<AZOAResult<IEnumerable<ActionAccessRequest>>> GetByRequesterAsync(
        Guid requesterUserId, ActionAccessRequestStatus? status = null, CancellationToken ct = default);

    /// <summary>
    /// The single non-terminal (Pending) request for a (action, requester) pair, if any.
    /// Backs the ≤1-Pending idempotency invariant: <c>IsError</c> (not found) when
    /// none is open.
    /// </summary>
    Task<AZOAResult<ActionAccessRequest>> GetPendingForActionAndRequesterAsync(
        Guid actionId, Guid requesterUserId, CancellationToken ct = default);

    /// <summary>Updates an existing request (status transition, decision fields).</summary>
    Task<AZOAResult<ActionAccessRequest>> UpdateAsync(ActionAccessRequest request, CancellationToken ct = default);
}
