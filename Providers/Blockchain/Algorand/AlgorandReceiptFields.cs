using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Matches typed fields in authoritative Algorand receipt objects.</summary>
internal static class AlgorandReceiptFields
{
    public static bool Number(JsonElement element, string field, ulong expected)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) && number == expected;

    public static bool String(JsonElement element, string field, string expected)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
}
