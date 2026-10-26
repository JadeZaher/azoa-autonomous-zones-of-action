using System.Security.Cryptography;
using Nethereum.Signer;

namespace AZOA.WebAPI.IntegrationTests;

/// <summary>Exports a fixture <see cref="EthECKey"/> as a fresh, exactly-32-byte scalar; see Fixtures/Ethereum/AGENTS.md §fixture-key-export.</summary>
internal static class EthereumFixtureKey
{
    /// <summary>Raw secp256k1 private key length the provider requires.</summary>
    public const int PrivateKeyLength = 32;

    /// <summary>Left-pads Nethereum's minimal big-endian export to 32 bytes; never trims. Caller owns (and zeroes) the copy.</summary>
    public static byte[] PrivateKeyBytes(EthECKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var minimal = key.GetPrivateKeyAsBytes();
        if (minimal.Length > PrivateKeyLength)
            throw new InvalidOperationException(
                $"Fixture key exported {minimal.Length} bytes; a secp256k1 scalar is at most {PrivateKeyLength}.");
        var padded = new byte[PrivateKeyLength];
        minimal.CopyTo(padded, PrivateKeyLength - minimal.Length);
        return padded;
    }

    /// <summary>The 0x-prefixed 64-hex-digit form the provider's config door parses.</summary>
    public static string PrivateKeyHex(EthECKey key)
    {
        var bytes = PrivateKeyBytes(key);
        try { return "0x" + Convert.ToHexStringLower(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
