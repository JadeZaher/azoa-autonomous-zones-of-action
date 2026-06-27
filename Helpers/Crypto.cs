using System.Security.Cryptography;

namespace AZOA.WebAPI.Helpers;

/// <summary>
/// Shared cryptographic helpers promoted from private forks.
/// </summary>
public static class Crypto
{
    /// <summary>Cryptographically-random URL-safe nonce (256 bits).</summary>
    public static string GenerateNonce()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
