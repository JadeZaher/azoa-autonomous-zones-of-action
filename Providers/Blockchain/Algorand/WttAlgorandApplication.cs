using System.Security.Cryptography;
using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Exact reviewed application programs and binary-keyed protocol globals.</summary>
internal sealed record WttAlgorandApplication(IReadOnlyDictionary<string, WttAlgorandApplication.Value> Globals)
{
    internal sealed record Value(ulong? Integer, byte[]? Bytes);

    internal static WttAlgorandApplication? Read(JsonElement application, ulong id, string approvalHash, string clearHash)
    {
        try
        {
            _ = CanonicalJson.Canonicalize(application);
            if (application.ValueKind != JsonValueKind.Object || !application.TryGetProperty("id", out var identifier)
                || identifier.ValueKind != JsonValueKind.Number || !identifier.TryGetUInt64(out var actualId) || actualId != id
                || !application.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
                || !Matches(parameters, "approval-program", approvalHash) || !Matches(parameters, "clear-state-program", clearHash)
                || !parameters.TryGetProperty("global-state", out var globals) || globals.ValueKind != JsonValueKind.Array
                || globals.GetArrayLength() > 64) return null;
            var entries = new Dictionary<string, Value>(StringComparer.Ordinal);
            foreach (var entry in globals.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("key", out var key)
                    || key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: > 0 and <= 88 } encodedKey
                    || !entry.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number
                    || !type.TryGetUInt64(out var kind)) return null;
                var decodedKey = Convert.FromBase64String(encodedKey);
                if (decodedKey.Length is 0 or > 64 || Convert.ToBase64String(decodedKey) != encodedKey) return null;
                Value parsed;
                if (kind == 1 && value.TryGetProperty("bytes", out var data) && data.ValueKind == JsonValueKind.String
                    && data.GetString() is { Length: <= 172 } encoded)
                {
                    var bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length > 128 || Convert.ToBase64String(bytes) != encoded) return null;
                    parsed = new(null, bytes);
                }
                else if (kind == 2 && value.TryGetProperty("uint", out var number) && number.ValueKind == JsonValueKind.Number
                    && number.TryGetUInt64(out var integer)) parsed = new(integer, null);
                else return null;
                if (!entries.TryAdd(Convert.ToHexStringLower(decodedKey), parsed)) return null;
            }
            return new(entries);
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    internal Value? Get(ReadOnlySpan<byte> key) => Globals.GetValueOrDefault(Convert.ToHexStringLower(key));

    private static bool Matches(JsonElement parameters, string name, string expected)
    {
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigitLower)
            || !parameters.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { Length: > 0 and <= 16384 } encoded) return false;
        var bytes = Convert.FromBase64String(encoded);
        return Convert.ToBase64String(bytes) == encoded && Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected;
    }
}
