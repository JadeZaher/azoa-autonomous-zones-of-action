using System.Security.Cryptography;
using Nethereum.Signer;
using Solnet.Wallet;
using Solnet.Wallet.Bip39;

namespace AZOA.WebAPI.Services.Signing;

/// <summary>
/// Generates cryptographic keypairs for supported chains and encrypts private keys
/// for secure storage. Uses AES-256-GCM with a derived key from an app secret.
/// </summary>
public class WalletKeyService
{
    /// <summary>Byte length of a raw secp256k1 private key — the persisted Ethereum scalar.</summary>
    public const int Secp256k1PrivateKeyLength = 32;

    /// <summary>Byte length of an uncompressed secp256k1 public key: <c>0x04 ‖ X(32) ‖ Y(32)</c>.</summary>
    public const int Secp256k1UncompressedPublicKeyLength = 65;

    /// <summary>How many scalars to draw before giving up on an out-of-range CSPRNG run.</summary>
    private const int MaxSecp256k1ScalarDraws = 8;

    private readonly byte[] _encryptionKey;

    public WalletKeyService(IConfiguration config)
    {
        var secret = config.GetValue<string>("AZOA:WalletEncryptionKey")
                     ?? throw new InvalidOperationException(
                         "AZOA:WalletEncryptionKey is required for platform wallet generation.");

        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("AZOA:WalletEncryptionKey cannot be empty.");

        // Derive a 256-bit key from the secret using SHA-256
        _encryptionKey = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>
    /// Generate a keypair for the given chain type.
    /// Returns (publicKey, privateKeyHex, address, seedPhrase).
    /// </summary>
    public (string publicKey, string privateKeyHex, string address, string? seedPhrase) GenerateKeypair(string chainType)
    {
        return chainType.ToLowerInvariant() switch
        {
            "algorand" => GenerateAlgorandKeypair(),
            "solana" => GenerateSolanaKeypair(),
            "ethereum" => GenerateEthereumKeypair(),
            _ => throw new NotSupportedException($"Chain type '{chainType}' is not supported for wallet generation.")
        };
    }

    /// <summary>Generate the development-only tenant keypair without a recovery phrase.</summary>
    public (string publicKey, string privateKeyHex, string address) GenerateCustodialKeypair(string chainType)
    {
        if (!string.Equals(chainType, "Algorand", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "Tenant custodial bootstrap currently supports Algorand development simulation only.");

        var generated = GenerateAlgorandKeypair(includeSeedPhrase: false);
        return (generated.publicKey, generated.privateKeyHex, generated.address);
    }

    /// <summary>Authenticates a purpose-qualified secret envelope without exposing its contents.</summary>
    public string FingerprintSecretEnvelope(string envelope)
        => Convert.ToHexString(HMACSHA256.HashData(_encryptionKey, System.Text.Encoding.UTF8.GetBytes(envelope))).ToLowerInvariant();

    /// <summary>Encrypt an authenticated application secret envelope with the platform key.</summary>
    public string EncryptSecretEnvelope(string envelope) => AesGcmEncrypt(envelope);

    /// <summary>Decrypt an application secret envelope; the caller must validate its purpose and identity.</summary>
    public string DecryptSecretEnvelope(string encryptedEnvelope) => AesGcmDecrypt(encryptedEnvelope);

    /// <summary>Encrypt a private key hex string for storage.</summary>
    public string EncryptPrivateKey(string privateKeyHex)
    {
        return AesGcmEncrypt(privateKeyHex);
    }

    /// <summary>Decrypt a stored encrypted private key.</summary>
    public string DecryptPrivateKey(string encryptedHex)
    {
        return AesGcmDecrypt(encryptedHex);
    }

    /// <summary>
    /// Decrypt a stored private key straight into a zeroable <see cref="byte"/> array
    /// of the RAW key bytes — the cleartext key never materializes as an immutable
    /// <c>string</c> (final-hardening B5). The caller MUST
    /// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> the returned
    /// buffer after use. See Services/Signing/AGENTS.md §decrypt-bytes.
    /// </summary>
    public byte[] DecryptPrivateKeyBytes(string encryptedHex)
    {
        // The plaintext is the private key hex (ASCII). Decrypt into an owned byte[]
        // (no plaintext string), then decode hex→raw bytes, zeroing the transient
        // ASCII buffer. Only the returned raw-key buffer escapes; the caller zeroes it.
        byte[] hexAscii = AesGcmDecryptBytes(encryptedHex);
        try
        {
            return DecodeHexAsciiToRaw(hexAscii);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hexAscii);
        }
    }

    /// <summary>Encrypt a seed phrase for storage.</summary>
    public string EncryptSeedPhrase(string seedPhrase)
    {
        return AesGcmEncrypt(seedPhrase);
    }

    /// <summary>Decrypt a stored encrypted seed phrase.</summary>
    public string DecryptSeedPhrase(string encryptedHex)
    {
        return AesGcmDecrypt(encryptedHex);
    }

    // ─── Chain-specific generation ───

    private (string publicKey, string privateKeyHex, string address, string? seedPhrase) GenerateAlgorandKeypair(
        bool includeSeedPhrase = true)
    {
        // Real Ed25519 keypair via the already-referenced Algorand2 package
        // (signing-core-keystone B1). Algorand2 owns the canonical
        // address/checksum/mnemonic rules — no hand-rolled curve math.
        var account = new Algorand.Algod.Model.Account();

        // The private key we persist is Algorand2's clear-text Ed25519 private key
        // bytes. AlgorandTransactionSigner reconstructs the signing account via
        // `new Account(privateKeyBytes)`, so keygen and signing share this exact
        // representation — the round-trip test pins that invariant.
        var privateKeyBytes = account.KeyPair.ClearTextPrivateKey;
        var publicKeyBytes = account.KeyPair.ClearTextPublicKey;

        var privateKeyHex = AZOA.WebAPI.Helpers.ByteEncoding.ToLowerHex(privateKeyBytes);
        var publicKeyHex = AZOA.WebAPI.Helpers.ByteEncoding.ToLowerHex(publicKeyBytes);
        var address = account.Address.EncodeAsString();   // real SHA-512/256 checksum + base32
        var seedPhrase = includeSeedPhrase ? account.ToMnemonic() : null;

        return (publicKeyHex, privateKeyHex, address, seedPhrase);
    }

    private (string, string, string, string?) GenerateSolanaKeypair()
    {
        // Real Ed25519 Solana keypair via the already-referenced Solnet.Wallet
        // (final-hardening B1). Generated from a restorable BIP39 mnemonic so the
        // seed phrase re-imports to the same address; Solnet owns the curve math,
        // BIP39 wordlist, and base58 address rules — no hand-rolled crypto.
        var mnemonic = new Mnemonic(WordList.English, WordCount.Twelve);
        var wallet = new Wallet(mnemonic);
        var account = wallet.Account;

        // The persisted private key is Solnet's 64-byte secret (32-byte Ed25519
        // seed ++ 32-byte public key). SolanaTransactionSigner reconstructs the
        // signing account via `new Account(secret64, secret64[32..64])`, so keygen
        // and signing share this exact representation — the round-trip test pins it.
        var secretBytes = account.PrivateKey.KeyBytes;   // 64 bytes
        var publicKeyBytes = account.PublicKey.KeyBytes; // 32 bytes

        var privateKeyHex = AZOA.WebAPI.Helpers.ByteEncoding.ToLowerHex(secretBytes);
        var publicKeyHex = AZOA.WebAPI.Helpers.ByteEncoding.ToLowerHex(publicKeyBytes);
        var address = account.PublicKey.Key;             // real base58-encoded public key
        var seedPhrase = mnemonic.ToString();            // restorable 12-word BIP39 mnemonic

        return (publicKeyHex, privateKeyHex, address, seedPhrase);
    }

    private (string, string, string, string?) GenerateEthereumKeypair()
    {
        // Real secp256k1 keypair (chain-value-routes H1). The persisted scalar is the
        // exact 32 bytes EthereumTransactionSigner reconstructs from, and the address is
        // DERIVED from that scalar — the prior placeholder hashed the key with HMAC-SHA256
        // and SHA-256, so wallet.Address was not the address the key controlled.
        Exception? lastDrawError = null;
        for (var attempt = 0; attempt < MaxSecp256k1ScalarDraws; attempt++)
        {
            // Keep the drawn bytes verbatim rather than re-encoding a big-integer form:
            // the signer fails closed on anything that is not exactly 32 bytes, and an
            // unsigned big-endian re-encoding would strip a leading zero byte.
            var privateKeyBytes = RandomNumberGenerator.GetBytes(Secp256k1PrivateKeyLength);
            byte[] uncompressedPublicKey;
            string address;
            try
            {
                (uncompressedPublicKey, address) = DeriveEthereumPublicKeyAndAddress(privateKeyBytes);
            }
            catch (Exception ex)
            {
                // A scalar outside [1, n-1] is astronomically unlikely; redraw rather
                // than persist a key that could never sign. Never echo the key bytes.
                CryptographicOperations.ZeroMemory(privateKeyBytes);
                lastDrawError = ex;
                continue;
            }

            // No recovery phrase for Ethereum: a phrase that restores in a standard
            // wallet needs BIP-39 + BIP-32/BIP-44 (m/44'/60'/0'/0/0) derivation, which no
            // referenced package provides. Null is the honest answer — the phrase this
            // used to return restored nothing AND leaked scalar bytes (each word was an
            // index over the key). See Services/Signing/AGENTS.md §ethereum-derivation.
            return (AZOA.WebAPI.Helpers.ByteEncoding.ToLowerHex(uncompressedPublicKey),
                    AZOA.WebAPI.Helpers.ByteEncoding.ToLowerHex(privateKeyBytes),
                    address,
                    null);
        }

        throw new InvalidOperationException(
            $"Failed to draw a valid secp256k1 private key in {MaxSecp256k1ScalarDraws} attempts.",
            lastDrawError);
    }

    // ─── Secp256k1 public key + Ethereum address derivation ───

    /// <summary>
    /// Derives the uncompressed secp256k1 public key (<c>0x04 ‖ X ‖ Y</c>) and the EIP-55
    /// checksummed address that a raw 32-byte private scalar controls.
    /// </summary>
    /// <remarks>
    /// <c>address = keccak-256(uncompressedPublicKey[1..])[12..]</c>, EIP-55 cased. The
    /// derivation runs through the same <see cref="EthECKey"/> that
    /// <c>EthereumTransactionSigner</c> signs with and <c>EthereumProvider</c> derives
    /// from, so a generated wallet's stored address is exactly the sender its key
    /// recovers to. See <c>Services/Signing/AGENTS.md</c> §ethereum-derivation.
    /// </remarks>
    public static (byte[] uncompressedPublicKey, string address) DeriveEthereumPublicKeyAndAddress(byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (privateKey.Length != Secp256k1PrivateKeyLength)
            throw new ArgumentException(
                $"An Ethereum private key must be exactly {Secp256k1PrivateKeyLength} bytes.", nameof(privateKey));

        var key = new EthECKey(privateKey, isPrivate: true);
        return (key.GetPubKey(false), key.GetPublicAddress());
    }

    // ─── AES-256-GCM encryption ───

    private string AesGcmEncrypt(string plaintext)
    {
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12); // 96-bit nonce for GCM
        var tag = new byte[16];
        var ciphertext = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack as: nonce (12) + tag (16) + ciphertext
        var result = new byte[12 + 16 + ciphertext.Length];
        Array.Copy(nonce, 0, result, 0, 12);
        Array.Copy(tag, 0, result, 12, 16);
        Array.Copy(ciphertext, 0, result, 28, ciphertext.Length);

        return AZOA.WebAPI.Helpers.ByteEncoding.ToLowerHex(result);
    }

    private string AesGcmDecrypt(string encryptedHex)
    {
        var plaintext = AesGcmDecryptBytes(encryptedHex);
        try
        {
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// AES-256-GCM decrypt into an owned plaintext <see cref="byte"/> array — no
    /// intermediate managed string. On a tag/format failure it throws AFTER zeroing
    /// any transient buffer (fail-closed, no partial plaintext leak). The caller
    /// owns and must zero the returned buffer.
    /// </summary>
    private byte[] AesGcmDecryptBytes(string encryptedHex)
    {
        var data = Convert.FromHexString(encryptedHex);
        if (data.Length < 28)
            throw new InvalidOperationException("Invalid encrypted data format.");

        var nonce = data[..12];
        var tag = data[12..28];
        var ciphertext = data[28..];

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_encryptionKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch
        {
            // Never return a partial/garbage plaintext buffer on auth failure.
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        return plaintext;
    }

    /// <summary>
    /// Decode an ASCII-hex plaintext buffer into the raw key bytes, mirroring the
    /// custody layer's <c>FromHexOrUtf8</c> contract: a hex payload decodes to its
    /// bytes; a non-hex payload (e.g. a mnemonic) returns its own bytes verbatim.
    /// Neither branch allocates a plaintext string.
    /// </summary>
    private static byte[] DecodeHexAsciiToRaw(byte[] hexAscii)
    {
        if (hexAscii.Length == 0 || (hexAscii.Length % 2) != 0 || !IsAsciiHex(hexAscii))
            return (byte[])hexAscii.Clone(); // non-hex (e.g. mnemonic bytes) — verbatim

        var raw = new byte[hexAscii.Length / 2];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = (byte)((HexNibble(hexAscii[2 * i]) << 4) | HexNibble(hexAscii[2 * i + 1]));
        return raw;
    }

    private static bool IsAsciiHex(byte[] ascii)
    {
        foreach (var b in ascii)
        {
            bool isHex = (b >= (byte)'0' && b <= (byte)'9')
                      || (b >= (byte)'a' && b <= (byte)'f')
                      || (b >= (byte)'A' && b <= (byte)'F');
            if (!isHex) return false;
        }
        return true;
    }

    private static int HexNibble(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => c - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - (byte)'A' + 10,
        _ => throw new FormatException("Invalid hex nibble.")
    };
}
