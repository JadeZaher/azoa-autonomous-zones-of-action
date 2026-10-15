namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// A requester user's self-service request to run an InviteOnly action.
/// Owner approval mints an invitation (appends to ActionDefinition.InvitedUserIds).
/// State machine + idempotency invariant live in
/// Persistence/SurrealDb/Models/AGENTS.md §action-access-request.
/// </summary>
public class ActionAccessRequest
{
    public Guid Id { get; set; }
    public Guid ActionId { get; set; }
    public Guid RequesterUserId { get; set; }

    public ActionAccessRequestStatus Status { get; set; } = ActionAccessRequestStatus.Pending;

    /// <summary>Optional requester note supplied at request time.</summary>
    public string? Message { get; set; }

    /// <summary>Optional owner-supplied reason recorded on approve/reject.</summary>
    public string? DecisionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Wall-clock time the request reached a terminal state (null while Pending).</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>User that decided the request (owner for approve/reject; requester for withdraw). Null while Pending.</summary>
    public Guid? DecidedByUserId { get; set; }
}
