// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Helpers;

/// <summary>
/// Reads and writes a <see cref="RailAmount"/> as its canonical decimal base-unit
/// string. Successor to <c>UlongDecimalStringJsonConverter</c> on the widened (D13)
/// amount surfaces; the wire form is byte-identical, so no caller sees a change.
/// See <c>Helpers/AGENTS.md</c> §rail-amount-json.
/// </summary>
public sealed class RailAmountBaseUnitStringJsonConverter : JsonConverter<RailAmount>
{
    public override RailAmount Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && RailAmount.TryParse(reader.GetString(), ValueDimension.NativeChainAsset, out var parsed))
        {
            return parsed;
        }

        // A JSON number is accepted for inbound compatibility only; anything above
        // 2^53 has already lost precision in the producer's own number type, so the
        // string form remains the contract this converter writes.
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out var number))
            return RailAmount.OfChainAsset(number);

        throw new JsonException("Expected a non-negative decimal base-unit string.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        RailAmount value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToBaseUnitString());

    /// <summary>Canonical decimal rendering of a raw base-unit count, for callers holding a bare <see cref="BigInteger"/>.</summary>
    public static string ToBaseUnitString(BigInteger baseUnits)
        => baseUnits.ToString(CultureInfo.InvariantCulture);
}
