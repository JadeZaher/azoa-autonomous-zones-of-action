namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Result of a successful wallet verify / claim (user-sovereign-identity AC1/AC2/AC3):
/// the standard login JWT the rest of the API accepts, plus the resolved user id.
/// </summary>
public class WalletAuthTokenResponse
{
    /// <summary>The login JWT (same shape <c>UserManager</c> issues for password login).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The user the token authenticates as (newly-created or wallet-bound).</summary>
    public Guid UserId { get; set; }
}
