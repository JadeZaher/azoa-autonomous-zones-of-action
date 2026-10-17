using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Solana address admission by role; see AGENTS.md §Address roles.</summary>
internal static class SolanaAddress
{
    private const int PublicKeyLength = 32;
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>Parses a canonical base58 32-byte key, failing closed on anything else.</summary>
    public static bool TryParse(string? address, out PublicKey publicKey)
    {
        publicKey = null!;
        if (string.IsNullOrWhiteSpace(address) || address.Length is < 32 or > 44 || !address.All(Base58Alphabet.Contains))
            return false;
        try
        {
            var decoded = Encoders.Base58.DecodeData(address);
            if (decoded.Length != PublicKeyLength || Encoders.Base58.EncodeData(decoded) != address) return false;
            publicKey = new PublicKey(decoded);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>A non-signing role (recipient, token account, mint, program); program-derived keys are valid.</summary>
    public static bool IsAccount(string? address) => TryParse(address, out _);

    /// <summary>A role that must sign; program-derived (off-curve) keys have no private key and are refused.</summary>
    public static bool IsSigner(string? address) => TryParse(address, out var publicKey) && publicKey.IsOnCurve();
}
