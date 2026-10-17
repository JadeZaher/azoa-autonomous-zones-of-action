using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Lossless ASA identity and metadata from an authoritative Algod asset response.</summary>
internal sealed record WttAlgorandAsset(ulong Id, string Creator, byte Decimals, ulong Total,
    string Manager, string Reserve, string Freeze, string Clawback, bool DefaultFrozen,
    string SymbolHex, string NameHex)
{
    internal static WttAlgorandAsset Read(JsonElement response, ulong expectedId)
    {
        _ = CanonicalJson.Canonicalize(response);
        if (expectedId == 0 || Unsigned(response, "index") != expectedId
            || !response.TryGetProperty("params", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Algorand asset response does not identify the requested ASA.");
        var decimals = Unsigned(value, "decimals");
        if (decimals > 19) throw new InvalidDataException("Algorand ASA decimals exceed the protocol bound.");
        var frozen = false;
        if (value.TryGetProperty("default-frozen", out var frozenValue))
        {
            if (frozenValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("ASA default freeze flag is malformed.");
            frozen = frozenValue.GetBoolean();
        }
        return new(expectedId, Address(value, "creator", required: true), (byte)decimals,
            Unsigned(value, "total"), Address(value, "manager"), Address(value, "reserve"),
            Address(value, "freeze"), Address(value, "clawback"), frozen,
            Metadata(value, "unit-name", 8), Metadata(value, "name", 32));
    }

    private static string Metadata(JsonElement value, string key, int maximum)
    {
        byte[] bytes = [];
        if (value.TryGetProperty(key + "-b64", out var encoded))
        {
            if (encoded.ValueKind != JsonValueKind.String || encoded.GetString()!.Length > 44)
                throw new InvalidDataException("ASA metadata bytes are malformed.");
            try { bytes = Convert.FromBase64String(encoded.GetString()!); }
            catch (FormatException) { throw new InvalidDataException("ASA metadata bytes are malformed."); }
            if (Convert.ToBase64String(bytes) != encoded.GetString())
                throw new InvalidDataException("ASA metadata bytes are not canonical base64.");
        }
        if (value.TryGetProperty(key, out var text))
        {
            if (text.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("ASA metadata display value is malformed.");
            var displayed = System.Text.Encoding.UTF8.GetBytes(text.GetString()!);
            if (value.TryGetProperty(key + "-b64", out _) && !displayed.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("ASA metadata display value conflicts with its raw bytes.");
            bytes = displayed;
        }
        if (bytes.Length > maximum) throw new InvalidDataException("ASA metadata exceeds its protocol field length.");
        var padded = new byte[32];
        bytes.CopyTo(padded, 0);
        return Convert.ToHexStringLower(padded);
    }

    private static string Address(JsonElement value, string key, bool required = false)
    {
        if (!value.TryGetProperty(key, out var field))
        {
            if (required) throw new InvalidDataException("ASA creator is absent.");
            return new global::Algorand.Address(new byte[32]).ToString();
        }
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: 58 } text
            || !global::Algorand.Address.IsValid(text))
            throw new InvalidDataException("ASA authority address is malformed.");
        return text;
    }

    private static ulong Unsigned(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var number) ? number
            : throw new InvalidDataException("ASA unsigned field is missing or malformed: " + key);
}
