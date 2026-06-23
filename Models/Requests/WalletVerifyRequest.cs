namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>POST api/avatar/auth/verify</c> (user-sovereign-identity AC1/AC1b).
/// The user submits the signature over the challenge's domain message. The server
/// resolves the live challenge for <c>(Address, ChainType)</c> and re-validates every
/// field; <see cref="Message"/> is an OPTIONAL exact-equality cross-check echo of the
/// signed bytes (rejected on any mismatch with the server-stored domain message).
/// </summary>
public class WalletVerifyRequest
{
    /// <summary>The wallet address that produced the signature.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The chain type the challenge was issued for.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The signature over the challenge domain message (chain-native encoding; base64 or hex).</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// OPTIONAL echo of the exact signed message. When present it MUST equal the
    /// server-stored domain message byte-for-byte; any mismatch rejects the verify.
    /// Omitting it is fine — the server resolves the canonical message itself.
    /// </summary>
    public string? Message { get; set; }
}
