using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical JSON encoding for immutable application intents; see Helpers/AGENTS.md.</summary>
public static class CanonicalJson
{
    /// <summary>Sorts object keys ordinally while preserving array order and number tokens.</summary>
    public static string Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Hashes the canonical UTF-8 JSON encoding.</summary>
    public static string Hash(JsonElement value) => HashCanonical(Canonicalize(value));

    /// <summary>Serializes a value using the platform JSON defaults and hashes its canonical encoding.</summary>
    public static string Hash(object value) => Hash(JsonSerializer.SerializeToElement(value));

    /// <summary>Hashes exact previously canonicalized and persisted UTF-8 JSON bytes.</summary>
    public static string HashCanonical(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new JsonException("Canonical intents cannot contain duplicate object keys.");
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("An undefined value cannot be a canonical intent.");
        }
    }
}
