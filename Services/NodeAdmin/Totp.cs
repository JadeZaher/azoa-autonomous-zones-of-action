using System.Security.Cryptography;
using System.Text;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30 s steps) with RFC 4648 base32 secrets, Google Authenticator compatible.</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The RFC 6238 time step containing <paramref name="instant"/>.</summary>
    public static long StepAt(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>RFC 4226 HOTP value for one counter, zero-padded to <paramref name="digits"/>.</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[19] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Matches <paramref name="code"/> within ±1 step of now, only for steps after <paramref name="lastUsedStep"/>; returns the matched step.</summary>
    public static bool TryVerify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit)) return false;
        var current = StepAt(now);
        var supplied = Encoding.ASCII.GetBytes(code);
        var found = false;
        for (var step = current - 1; step <= current + 1; step++)
        {
            var expected = Encoding.ASCII.GetBytes(Code(secret, step));
            // Evaluate every window step so timing does not reveal which one matched.
            if (CryptographicOperations.FixedTimeEquals(expected, supplied) && step > lastUsedStep && !found)
            {
                matchedStep = step;
                found = true;
            }
        }
        return found;
    }

    /// <summary>A fresh 160-bit secret.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>RFC 4648 base32 without padding.</summary>
    public static string ToBase32(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    /// <summary>The otpauth provisioning URI of the binding contract (plan §8, CLI).</summary>
    public static string ProvisioningUri(string host, string username, string secretBase32)
    {
        var issuer = Uri.EscapeDataString($"AZOA Node ({host})");
        return $"otpauth://totp/{issuer}:{Uri.EscapeDataString(username)}?secret={secretBase32}&issuer={issuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
