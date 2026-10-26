using System.Security.Cryptography;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical identity for the one platform wallet per user and chain.</summary>
public static class WalletBootstrapIdentity
{
    /// <summary>The network-free chain; minting a key for it is gated on Blockchain:Mode at the wallet seam (see Managers/AGENTS.md §simulated-chain-wallets).</summary>
    public const string SimulatedChain = "Simulated";

    public static string? CanonicalChain(string? chainType) => chainType?.Trim().ToLowerInvariant() switch
    {
        "algorand" => "Algorand",
        "solana" => "Solana",
        "ethereum" => "Ethereum",
        "simulated" => SimulatedChain,
        _ => null
    };

    public static Guid For(Guid userId, string canonicalChain)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalChain);

        var material = System.Text.Encoding.UTF8.GetBytes(
            $"azoa:platform-wallet-bootstrap:v1:{userId:N}:{canonicalChain.ToLowerInvariant()}");
        var hash = SHA256.HashData(material);
        return new Guid(hash.AsSpan(0, 16));
    }
}
