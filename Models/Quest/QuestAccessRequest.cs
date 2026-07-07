namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// A requester avatar's self-service request to run an InviteOnly quest.
/// Owner approval mints an invitation (appends to Quest.InvitedAvatarIds).
/// State machine + idempotency invariant live in
/// Persistence/SurrealDb/Models/AGENTS.md §quest-access-request.
/// </summary>
public class QuestAccessRequest
{
    public Guid Id { get; set; }
    public Guid QuestId { get; set; }
    public Guid RequesterAvatarId { get; set; }

    public QuestAccessRequestStatus Status { get; set; } = QuestAccessRequestStatus.Pending;

    /// <summary>Optional requester note supplied at request time.</summary>
    public string? Message { get; set; }

    /// <summary>Optional owner-supplied reason recorded on approve/reject.</summary>
    public string? DecisionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Wall-clock time the request reached a terminal state (null while Pending).</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>Avatar that decided the request (owner for approve/reject; requester for withdraw). Null while Pending.</summary>
    public Guid? DecidedByAvatarId { get; set; }
}
