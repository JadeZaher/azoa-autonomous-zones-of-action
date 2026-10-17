using System.Numerics;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Canonical intent and exact quoted cost validation shared by native modules.</summary>
public static class NativeOperationAuthorization
{
    public static string IntentHash(NativeOperationIntent intent)
        => CanonicalJson.Hash(intent with { Budget = intent.Budget with { QuoteId = "" }, Quote = null });

    public static bool Amount(string value, out BigInteger amount)
        => UnsignedIntegerText.TryParse(value, out amount);

    public static bool ValidQuote(NativeOperationIntent intent, bool requireUnexpired = true)
        => intent.Version == 1 && intent.Quote is { } quote
            && quote.QuoteId == intent.Budget.QuoteId && quote.IntentHash == IntentHash(intent)
            && quote.ProtocolIdentity == intent.ProtocolIdentity
            && (!requireUnexpired || quote.ExpiresAt > DateTimeOffset.UtcNow)
            && Amount(quote.NetworkFee, out var fee) && Amount(intent.Budget.MaximumNetworkFee, out var feeCap) && fee <= feeCap
            && Amount(quote.LockedRent, out var rent) && Amount(intent.Budget.MaximumLockedRent, out var rentCap) && rent <= rentCap
            && Amount(quote.Value, out var value) && Amount(intent.Budget.MaximumValue, out var valueCap) && value <= valueCap;
}
