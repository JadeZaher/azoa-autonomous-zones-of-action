using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>Body for <c>PUT /api/action/{id}/run-access</c> — owner sets run-authorization mode + optionally seeds the invite list. See Managers/AGENTS.md §action-invitations.</summary>
public class ActionRunAccessRequest
{
    public ActionRunAccess RunAccess { get; set; } = ActionRunAccess.Open;

    /// <summary>Optional invite set to seed when switching to InviteOnly; null = leave the existing InvitedUserIds unchanged.</summary>
    public List<Guid>? InvitedUserIds { get; set; }
}

/// <summary>Body for <c>POST /api/action/{id}/invite</c> — owner directly adds an invite (idempotent).</summary>
public class ActionInviteRequest
{
    public Guid UserId { get; set; }
}

/// <summary>Body for <c>POST /api/action/{id}/access-requests</c> — a viewer opens a Pending access request.</summary>
public class ActionAccessOpenRequest
{
    /// <summary>Optional requester note carried onto the ActionAccessRequest.</summary>
    public string? Message { get; set; }
}

/// <summary>Body for <c>POST /api/action/access-requests/{requestId}/decision</c> — owner approves (mints invite) or rejects a Pending request.</summary>
public class ActionAccessDecisionRequest
{
    public bool Approve { get; set; }

    /// <summary>Optional owner reason recorded on the decided request.</summary>
    public string? Reason { get; set; }
}
