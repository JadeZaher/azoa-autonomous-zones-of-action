namespace AZOA.WebAPI.Helpers;

/// <summary>
/// Shared byte/hex/text encoding helpers, promoted from identical private forks
/// across the wallet, custody, wormhole, and DEX layers. NOTE: this type
/// deliberately shadows <see cref="System.Text.Encoding"/> by short name — inside
/// this file the BCL type is referenced fully-qualified as
/// <c>System.Text.Encoding</c>.
/// </summary>
public static class Encoding
{
    /// <summary>Lowercase hex string of a byte buffer (canonical for the
    /// repo-wide <c>Convert.ToHexString(x).ToLowerInvariant()</c> idiom).</summary>
    public static string ToLowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static bool IsHex(string value)
    {
        if (value.Length == 0 || (value.Length % 2) != 0) return false;
        foreach (var c in value)
        {
            var isHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHexDigit) return false;
        }
        return true;
    }

    /// <summary>
    /// Decode a string to zeroable bytes. Hex payloads are decoded as hex; fall
    /// back to UTF-8 bytes for any non-hex payload (e.g. a mnemonic string) so the
    /// byte[] contract holds for every chain.
    /// </summary>
    public static byte[] FromHexOrUtf8(string value)
    {
        if (IsHex(value))
            return Convert.FromHexString(value);
        return System.Text.Encoding.UTF8.GetBytes(value);
    }

    /// <summary>Accept a signature as base64 (preferred) or hex; fail-closed otherwise.</summary>
    public static bool TryDecodeSignature(string signature, out byte[] bytes)
    {
        var s = signature.Trim();
        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            // not base64 — fall through to hex
        }

        try
        {
            var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    public static byte[] HexToBytes(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];
        return Convert.FromHexString(hex);
    }

    /// <summary>Big-endian bytes → ulong (right-aligned; tolerant of fewer than 8 bytes).</summary>
    public static ulong ReadBigEndianU64Loose(byte[] bytes)
    {
        ulong value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }
}
