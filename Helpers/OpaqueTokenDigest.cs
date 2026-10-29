using System.Security.Cryptography;

namespace AZOA.WebAPI.Helpers;

/// <summary>Versioned at-rest digest of a bearer token, <c>"h1:" + lowercase hex SHA-256</c>; see Helpers/AGENTS.md §opaque-token-digest.</summary>
public static class OpaqueTokenDigest
{
    /// <summary>Version tag of the only accepted stored form.</summary>
    public const string V1Prefix = "h1:";

    private const int Sha256HexLength = 64;

    /// <summary>The v1 storage form of <paramref name="token"/>.</summary>
    public static string Compute(string token)
        => V1Prefix + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>True only for a well-formed v1 digest; a legacy plaintext token is never one.</summary>
    public static bool IsV1Digest(string? stored)
        => stored is not null
           && stored.Length == V1Prefix.Length + Sha256HexLength
           && stored.StartsWith(V1Prefix, StringComparison.Ordinal)
           && !stored.AsSpan(V1Prefix.Length).ContainsAnyExcept("0123456789abcdef");

    /// <summary>Constant-time check that <paramref name="stored"/> is the v1 digest of <paramref name="presented"/>; legacy rows never match.</summary>
    public static bool Matches(string? stored, string? presented)
    {
        if (string.IsNullOrEmpty(presented) || !IsV1Digest(stored)) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(Compute(presented)),
            System.Text.Encoding.ASCII.GetBytes(stored!));
    }
}
