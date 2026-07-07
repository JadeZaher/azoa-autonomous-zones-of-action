using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence boundary for <see cref="QuestAccessRequest"/> — a requester
/// avatar's self-service request to run an InviteOnly <see cref="Quest"/>.
/// Introduced by the quest-invitations-approval track. State machine +
/// idempotency invariant live in
/// <c>Persistence/SurrealDb/Models/AGENTS.md §quest-access-request</c>.
/// </summary>
public interface IQuestAccessRequestStore
{
    /// <summary>Inserts a new access-request row.</summary>
    Task<AZOAResult<QuestAccessRequest>> CreateAsync(QuestAccessRequest request, CancellationToken ct = default);

    /// <summary>Loads a single request by id; <c>IsError</c> when not found.</summary>
    Task<AZOAResult<QuestAccessRequest>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>All requests for a quest, optionally filtered to a single status (owner approval queue).</summary>
    Task<AZOAResult<IEnumerable<QuestAccessRequest>>> GetByQuestAsync(
        Guid questId, QuestAccessRequestStatus? status = null, CancellationToken ct = default);

    /// <summary>All requests raised by a requester across any quest, optionally status-filtered.</summary>
    Task<AZOAResult<IEnumerable<QuestAccessRequest>>> GetByRequesterAsync(
        Guid requesterAvatarId, QuestAccessRequestStatus? status = null, CancellationToken ct = default);

    /// <summary>
    /// The single non-terminal (Pending) request for a (quest, requester) pair, if any.
    /// Backs the ≤1-Pending idempotency invariant: <c>IsError</c> (not found) when
    /// none is open.
    /// </summary>
    Task<AZOAResult<QuestAccessRequest>> GetPendingForQuestAndRequesterAsync(
        Guid questId, Guid requesterAvatarId, CancellationToken ct = default);

    /// <summary>Updates an existing request (status transition, decision fields).</summary>
    Task<AZOAResult<QuestAccessRequest>> UpdateAsync(QuestAccessRequest request, CancellationToken ct = default);
}
