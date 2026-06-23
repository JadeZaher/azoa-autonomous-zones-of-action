namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Result of a successful wallet verify / claim (user-sovereign-identity AC1/AC2/AC3):
/// the standard login JWT the rest of the API accepts, plus the resolved avatar id.
/// </summary>
public class WalletAuthTokenResponse
{
    /// <summary>The login JWT (same shape <c>AvatarManager</c> issues for password login).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The avatar the token authenticates as (newly-created or wallet-bound).</summary>
    public Guid AvatarId { get; set; }
}
