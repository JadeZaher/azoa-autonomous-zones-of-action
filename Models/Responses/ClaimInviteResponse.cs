using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Result of <c>POST api/user/claim-invite</c> (user-sovereign-identity AC4). The token is
/// delivered to the member, never to the tenant; see Services/Auth/AGENTS.md §claim-invites.
/// </summary>
public class ClaimInviteResponse
{
    /// <summary>Absent (omitted from JSON) unless the node sets <c>Tenant:ClaimInvites:ReturnTokenToTenant</c> (test harness only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimToken { get; set; }

    /// <summary>The child user this invite authorizes a claim for.</summary>
    public Guid TargetUserId { get; set; }

    /// <summary>Invite expiry (UTC); the member must redeem before this.</summary>
    public DateTime ExpiresAt { get; set; }
}
