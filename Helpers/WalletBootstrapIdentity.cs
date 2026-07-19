using System.Security.Cryptography;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical identity for the one platform wallet per avatar and chain.</summary>
public static class WalletBootstrapIdentity
{
    public static string? CanonicalChain(string? chainType) => chainType?.Trim().ToLowerInvariant() switch
    {
        "algorand" => "Algorand",
        "solana" => "Solana",
        "ethereum" => "Ethereum",
        _ => null
    };

    public static Guid For(Guid avatarId, string canonicalChain)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(avatarId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalChain);

        var material = System.Text.Encoding.UTF8.GetBytes(
            $"azoa:platform-wallet-bootstrap:v1:{avatarId:N}:{canonicalChain.ToLowerInvariant()}");
        var hash = SHA256.HashData(material);
        return new Guid(hash.AsSpan(0, 16));
    }
}
