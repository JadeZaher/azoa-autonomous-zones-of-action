namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>POST api/user/wallet/link</c> (user-sovereign-identity AC2b). Binds a
/// wallet to the ALREADY-AUTHENTICATED user. The user id comes from the caller's
/// JWT, NEVER from this body (IDOR rule) — this carries only the wallet proof.
/// </summary>
public class WalletLinkRequest
{
    /// <summary>The wallet address to bind to the authenticated user.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The chain type (e.g. <c>algorand</c>).</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The signature over the challenge domain message proving control of the wallet.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>OPTIONAL exact-equality echo of the signed domain message (see WalletVerifyRequest).</summary>
    public string? Message { get; set; }
}
