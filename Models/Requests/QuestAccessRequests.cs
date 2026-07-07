using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>Body for <c>PUT /api/quest/{id}/run-access</c> — owner sets run-authorization mode + optionally seeds the invite list. See Managers/AGENTS.md §quest-invitations.</summary>
public class QuestRunAccessRequest
{
    public QuestRunAccess RunAccess { get; set; } = QuestRunAccess.Open;

    /// <summary>Optional invite set to seed when switching to InviteOnly; null = leave the existing InvitedAvatarIds unchanged.</summary>
    public List<Guid>? InvitedAvatarIds { get; set; }
}

/// <summary>Body for <c>POST /api/quest/{id}/invite</c> — owner directly adds an invite (idempotent).</summary>
public class QuestInviteRequest
{
    public Guid AvatarId { get; set; }
}

/// <summary>Body for <c>POST /api/quest/{id}/access-requests</c> — a viewer opens a Pending access request.</summary>
public class QuestAccessOpenRequest
{
    /// <summary>Optional requester note carried onto the QuestAccessRequest.</summary>
    public string? Message { get; set; }
}

/// <summary>Body for <c>POST /api/quest/access-requests/{requestId}/decision</c> — owner approves (mints invite) or rejects a Pending request.</summary>
public class QuestAccessDecisionRequest
{
    public bool Approve { get; set; }

    /// <summary>Optional owner reason recorded on the decided request.</summary>
    public string? Reason { get; set; }
}
