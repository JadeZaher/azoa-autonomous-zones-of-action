namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Result of <c>POST api/avatar/auth/challenge</c> (user-sovereign-identity AC1/AC1b).
/// The client signs <see cref="Message"/> EXACTLY (the domain-separated bytes) and
/// returns the signature to <c>/auth/verify</c>.
/// </summary>
public class WalletChallengeResponse
{
    /// <summary>The address the challenge is bound to (echoed for client convenience).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The chain type the challenge is bound to.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The one-time server nonce embedded in <see cref="Message"/>.</summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>
    /// The EXACT human-readable bytes to sign (AC1b domain-separated message). The
    /// client must sign this verbatim; verify re-validates every embedded field.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Challenge expiry (UTC); the signature must be presented before this.</summary>
    public DateTime ExpiresAt { get; set; }
}
