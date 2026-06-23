namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>POST api/avatar/auth/challenge</c> (user-sovereign-identity AC1).
/// Requests a one-time nonce bound to <c>(Address, ChainType)</c> to sign.
/// </summary>
public class WalletChallengeRequest
{
    /// <summary>The wallet address to bind the challenge to (chain-native encoding).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The chain type (e.g. <c>algorand</c>).</summary>
    public string ChainType { get; set; } = string.Empty;
}
