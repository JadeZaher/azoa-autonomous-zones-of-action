extern alias BCCrypto2;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ed25519PublicKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Canonical signing strings, Ed25519 verification, fingerprints and encodings of the node-admin wire contract (plan §8 H2).</summary>
public static class NodeAdminCrypto
{
    public const string Domain = "azoa-node-admin/v1";

    /// <summary>Wire format of every server-issued timestamp that a client signs.</summary>
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK";

    public static string FormatTimestamp(DateTimeOffset instant)
        => instant.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Login challenge message: domain, purpose, audience, challenge id, nonce, expiresAt joined by LF.</summary>
    public static string LoginMessage(string purpose, string audience, string challengeId, string nonce, string expiresAt)
        => string.Join('\n', Domain, purpose, audience, challengeId, nonce, expiresAt);

    /// <summary>Enrollment proof message.</summary>
    public static string EnrollMessage(string audience, string enrollmentId)
        => string.Join('\n', Domain, "enroll", audience, enrollmentId);

    /// <summary>Proof of possession for an added key.</summary>
    public static string AddKeyMessage(string audience, string adminId, string newFingerprint)
        => string.Join('\n', Domain, "add_key", audience, adminId, newFingerprint);

    /// <summary>sha256(raw 32-byte key) as lowercase hex, first 16 characters.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey)
        => Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];

    public static bool IsFingerprint(string? value)
        => value is { Length: 16 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Decodes an unpadded base64url Ed25519 public key; false unless exactly 32 bytes.</summary>
    public static bool TryDecodePublicKey(string? value, out byte[] publicKey)
        => TryDecodeExact(value, 32, out publicKey);

    /// <summary>Decodes an unpadded base64url Ed25519 signature; false unless exactly 64 bytes.</summary>
    public static bool TryDecodeSignature(string? value, out byte[] signature)
        => TryDecodeExact(value, 64, out signature);

    /// <summary>Verifies a raw Ed25519 signature over the UTF-8 bytes of <paramref name="message"/>; malformed keys fail closed.</summary>
    public static bool Verify(byte[] publicKey, string message, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        Ed25519PublicKeyParameters key;
        try
        {
            key = new Ed25519PublicKeyParameters(publicKey, 0);
        }
        catch (ArgumentException)
        {
            return false;
        }
        var bytes = Encoding.UTF8.GetBytes(message);
        var verifier = new Ed25519Signer();
        verifier.Init(false, key);
        verifier.BlockUpdate(bytes, 0, bytes.Length);
        return verifier.VerifySignature(signature);
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    /// <summary>SHA-256 hex of an opaque secret (enrollment token storage form).</summary>
    public static string HashSecret(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string RandomToken(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>Fixed-time equality of two byte strings of any length.</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>Storage record id of a wire sid: sha256 hex of its 16 raw bytes; false unless the sid decodes to exactly 16 bytes.</summary>
    public static bool TrySessionRecordId(string? sid, out string recordId)
    {
        recordId = string.Empty;
        if (!TryDecodeExact(sid, 16, out var bytes)) return false;
        recordId = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return true;
    }

    /// <summary>True for a well-formed session handle (32 lowercase hex characters).</summary>
    public static bool IsSessionHandle(string? value)
        => value is { Length: 32 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>New session as (base64url sid for the token only, hashed record id, independent random handle).</summary>
    public static (string Sid, string RecordId, string Handle) NewSession()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return (Base64Url(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
    }

    private static bool TryDecodeExact(string? value, int length, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length > 256 || value.Contains('=')
            || !System.Buffers.Text.Base64Url.IsValid(value, out var decodedLength) || decodedLength != length)
            return false;
        bytes = System.Buffers.Text.Base64Url.DecodeFromChars(value);
        // Reject non-canonical encodings (stray trailing bits) so one key has exactly one spelling.
        return bytes.Length == length && string.Equals(Base64Url(bytes), value, StringComparison.Ordinal);
    }
}
