using System.Text.Json;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Fiat;

/// <summary>Creates idempotent refunds and reads their authoritative state.</summary>
public interface IStripeRefundGateway
{
    /// <summary>Refunds the exact original payment amount using a durable node-owned key.</summary>
    Task<AZOAResult<StripeRefund>> CreateRefundAsync(StripeCredentials credentials,
        string paymentIntentId, long amount, string intentId, string idempotencyKey, CancellationToken ct = default);
    /// <summary>Reads the provider refund; callback bodies are never settlement evidence.</summary>
    Task<AZOAResult<StripeRefund>> GetRefundAsync(StripeCredentials credentials, string refundId, CancellationToken ct = default);
}

/// <summary>Minimal provider refund evidence without payment instrument data.</summary>
public sealed record StripeRefund(string Id, string PaymentIntentId, long Amount, string Currency, string Status, string IntentId)
{
    public static StripeRefund? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("payment_intent", out var payment) || payment.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("amount", out var amount) || !amount.TryGetInt64(out var minorUnits)
                || !root.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
                || !metadata.TryGetProperty("azoa_intent_id", out var intent) || intent.ValueKind != JsonValueKind.String)
                return null;
            return new(id.GetString()!, payment.GetString()!, minorUnits, currency.GetString()!, status.GetString()!, intent.GetString()!);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
