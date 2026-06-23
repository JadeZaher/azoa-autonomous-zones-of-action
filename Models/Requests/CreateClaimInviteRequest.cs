namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>POST api/avatar/claim-invite</c> (user-sovereign-identity AC4). A tenant
/// mints a single-use, short-TTL claim token for a child it owns. The tenant id is
/// taken from the authenticated principal, NEVER this body (IDOR rule).
/// </summary>
public class CreateClaimInviteRequest
{
    /// <summary>The child avatar the tenant is inviting the user to claim (asserted owned).</summary>
    public Guid ChildAvatarId { get; set; }
}
