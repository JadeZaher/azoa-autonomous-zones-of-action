using System.Numerics;
using System.Security.Cryptography;
using Solnet.Wallet.Bip39;
using AlgoAccount = Algorand.Algod.Model.Account;
using SolanaWallet = Solnet.Wallet.Wallet;

namespace AZOA.WebAPI.Services.Custody;

/// <summary>Algorand platform key: 25-word mnemonic → 32-byte Ed25519 private key.</summary>
public sealed class AlgorandPlatformKeyDerivation : IPlatformKeyDerivation
{
    /// <summary>Config path of the Algorand platform mnemonic.</summary>
    public const string ConfigPath = "AZOA:Algorand:PlatformMnemonic";

    /// <summary>Algorand2 <c>ClearTextPrivateKey</c> length.</summary>
    public const int PrivateKeyLength = 32;

    public string ChainType => "Algorand";
    public string PlatformKeyConfigPath => ConfigPath;
    public string SecretDescription => "Algorand mnemonic";
    public int KeyLength => PrivateKeyLength;

    public bool TryDerive(string configuredSecret, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(configuredSecret))
            return false;

        try
        {
            // Same representation WalletKeyService persists for Algorand and
            // AlgorandTransactionSigner reconstructs (`new Account(bytes)`). Cloned so the
            // custody caller owns a buffer it can zero independently of the Account keypair.
            var derived = (byte[])new AlgoAccount(configuredSecret.Trim()).KeyPair.ClearTextPrivateKey.Clone();
            return PlatformKeyDerivationGuard.AcceptExactLength(derived, PrivateKeyLength, out key);
        }
        catch (Exception)
        {
            // Swallowed deliberately: the Algorand SDK echoes offending mnemonic words in
            // its exception messages. Nothing derived from the secret may escape.
            key = [];
            return false;
        }
    }
}

/// <summary>Solana platform key: BIP-39 mnemonic → Solnet 64-byte secret (seed ‖ public key).</summary>
public sealed class SolanaPlatformKeyDerivation : IPlatformKeyDerivation
{
    /// <summary>Config path of the Solana platform mnemonic.</summary>
    public const string ConfigPath = "AZOA:Solana:PlatformMnemonic";

    /// <summary>Solnet <c>PrivateKey.KeyBytes</c> length.</summary>
    public const int SecretKeyLength = 64;

    public string ChainType => "Solana";
    public string PlatformKeyConfigPath => ConfigPath;
    public string SecretDescription => "Solana BIP-39 mnemonic";
    public int KeyLength => SecretKeyLength;

    public bool TryDerive(string configuredSecret, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(configuredSecret))
            return false;

        try
        {
            // Byte-identical to SolanaProvider's own no-custody fallback import and to
            // WalletKeyService's Solana keygen, so the platform account is the same
            // account either door resolves.
            var derived = (byte[])new SolanaWallet(configuredSecret.Trim(), WordList.English)
                .Account.PrivateKey.KeyBytes.Clone();
            return PlatformKeyDerivationGuard.AcceptExactLength(derived, SecretKeyLength, out key);
        }
        catch (Exception)
        {
            // Swallowed deliberately: NBitcoin/Solnet report the offending word.
            key = [];
            return false;
        }
    }
}

/// <summary>
/// Ethereum platform key: 0x-prefixed 32-byte secp256k1 private key → the same bytes.
/// Deliberately NOT a mnemonic — a BIP-39 phrase needs a BIP-44 account path to become a
/// key, and guessing the path yields a valid-looking key for the wrong account.
/// </summary>
public sealed class EthereumPlatformKeyDerivation : IPlatformKeyDerivation
{
    /// <summary>Config path of the Ethereum platform private key (mirrors <c>EthereumProvider</c>).</summary>
    public const string ConfigPath = "AZOA:Ethereum:PlatformPrivateKey";

    /// <summary>Raw secp256k1 private key length.</summary>
    public const int PrivateKeyLength = 32;

    private const string HexPrefix = "0x";

    /// <summary>secp256k1 group order; a private key must be in [1, n-1].</summary>
    private static readonly BigInteger CurveOrder = BigInteger.Parse(
        "00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
        System.Globalization.NumberStyles.HexNumber);

    public string ChainType => "Ethereum";
    public string PlatformKeyConfigPath => ConfigPath;
    public string SecretDescription => "0x-prefixed 32-byte secp256k1 private key";
    public int KeyLength => PrivateKeyLength;

    public bool TryDerive(string configuredSecret, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(configuredSecret))
            return false;

        var text = configuredSecret.Trim();

        // The 0x prefix is required, matching EthereumProvider's own parse: the two doors
        // must accept exactly the same strings or they would resolve different senders.
        if (!text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = text[HexPrefix.Length..];
        if (digits.Length != PrivateKeyLength * 2)
            return false;

        foreach (var character in digits)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        byte[] derived;
        try
        {
            derived = Convert.FromHexString(digits);
        }
        catch (Exception)
        {
            return false;
        }

        // Reject a scalar outside [1, n-1] here rather than leaving it to the signer: an
        // out-of-range scalar either throws deep in a curve library or reduces to some
        // other key entirely. Big-endian, unsigned.
        var scalar = new BigInteger(derived, isUnsigned: true, isBigEndian: true);
        if (scalar <= BigInteger.Zero || scalar >= CurveOrder)
        {
            CryptographicOperations.ZeroMemory(derived);
            return false;
        }

        return PlatformKeyDerivationGuard.AcceptExactLength(derived, PrivateKeyLength, out key);
    }
}

/// <summary>Shared length gate: a wrong-length derivation is wiped, never handed out.</summary>
internal static class PlatformKeyDerivationGuard
{
    internal static bool AcceptExactLength(byte[] derived, int expectedLength, out byte[] key)
    {
        if (derived.Length != expectedLength)
        {
            CryptographicOperations.ZeroMemory(derived);
            key = [];
            return false;
        }

        key = derived;
        return true;
    }
}
