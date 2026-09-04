using System.Numerics;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Strict codecs for the two Ethereum JSON-RPC hex shapes: QUANTITY and DATA.</summary>
/// <remarks>See <c>Providers/Blockchain/Ethereum/AGENTS.md</c> §hex.</remarks>
public static class EthereumHex
{
    /// <summary>Byte width of a <c>uint256</c>; the widest quantity any Ethereum RPC field may carry.</summary>
    public const int MaxQuantityByteLength = 32;

    /// <summary>Largest value expressible in a <c>uint256</c> ABI slot.</summary>
    public static BigInteger MaxUInt256 { get; } = BigInteger.Pow(2, 256) - 1;

    /// <summary>Parses a JSON-RPC QUANTITY (<c>0x</c>-prefixed, unsigned, ≤ 32 bytes) into a <see cref="BigInteger"/>.</summary>
    public static bool TryParseQuantity(string? hex, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var text = hex.Trim();
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = text[2..];
        // Leading zeros are accepted on the READ side (nodes emit non-minimal forms);
        // the width cap is what keeps a hostile node from handing back a value that
        // could never fit a uint256 slot.
        if (digits.Length == 0 || digits.Length > MaxQuantityByteLength * 2)
            return false;
        if (!IsHex(digits))
            return false;

        var padded = digits.Length % 2 == 0 ? digits : "0" + digits;
        value = new BigInteger(Convert.FromHexString(padded), isUnsigned: true, isBigEndian: true);
        return true;
    }

    /// <summary>Encodes a non-negative <see cref="BigInteger"/> as a minimal JSON-RPC QUANTITY.</summary>
    public static string Quantity(BigInteger value)
    {
        if (value.Sign < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "An Ethereum JSON-RPC quantity is unsigned.");
        if (value.IsZero)
            return "0x0";

        var trimmed = Convert.ToHexStringLower(value.ToByteArray(isUnsigned: true, isBigEndian: true))
            .TrimStart('0');
        return "0x" + (trimmed.Length == 0 ? "0" : trimmed);
    }

    /// <summary>Encodes a byte payload as a JSON-RPC DATA string.</summary>
    public static string Data(ReadOnlySpan<byte> bytes) => "0x" + Convert.ToHexStringLower(bytes);

    /// <summary>Parses a JSON-RPC DATA string (<c>0x</c>-prefixed, even number of hex digits).</summary>
    public static bool TryParseData(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex is null)
            return false;

        var text = hex.Trim();
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = text[2..];
        if (digits.Length % 2 != 0 || !IsHex(digits))
            return false;

        bytes = digits.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(digits);
        return true;
    }

    /// <summary>True when every character is a hexadecimal digit.</summary>
    public static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}
