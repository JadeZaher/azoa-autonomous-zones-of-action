using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Helpers;

/// <summary>Writes unsigned 64-bit values as precision-safe decimal JSON strings.</summary>
public sealed class UlongDecimalStringJsonConverter : JsonConverter<ulong>
{
    public override ulong Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && ulong.TryParse(
                reader.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stringValue))
        {
            return stringValue;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out var numberValue))
            return numberValue;

        throw new JsonException("Expected an unsigned 64-bit decimal string.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ulong value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
