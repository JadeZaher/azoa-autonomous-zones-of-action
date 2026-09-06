namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Result of <c>POST api/user/claim-invite</c> (user-sovereign-identity AC4): the
/// single-use, short-TTL claim token the tenant hands to the user. The token alone
/// never sets a credential (M1) — the user redeems it at <c>/user/claim</c> with a
/// user-side secret.
/// </summary>
public class ClaimInviteResponse
{
    /// <summary>The single-use claim token to deliver to the user.</summary>
    public string ClaimToken { get; set; } = string.Empty;

    /// <summary>The child user this token authorizes a claim for.</summary>
    public Guid TargetUserId { get; set; }

    /// <summary>Token expiry (UTC); the user must redeem before this.</summary>
    public DateTime ExpiresAt { get; set; }
}
