using System.Text.Json;

namespace AZOA.WebAPI.Providers.Fiat;

/// <summary>Provider evidence for an idempotent funding reversal.</summary>
public sealed record StripeTransferReversal(string Id, string TransferId, long Amount)
{
    public static StripeTransferReversal? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && root.TryGetProperty("transfer", out var transfer) && transfer.ValueKind == JsonValueKind.String
                && root.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number && amount.TryGetInt64(out var minor)
                ? new(id.GetString()!, transfer.GetString()!, minor) : null;
        }
        catch (JsonException) { return null; }
    }
}
