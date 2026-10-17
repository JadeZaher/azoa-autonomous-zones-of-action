using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Binds accepted custody budgets to one immutable command and physical protocol.</summary>
public static class ValueCustodyIdentity
{
    /// <summary>Hashes complete quote terms independently of the quoted identifier.</summary>
    public static string QuoteId(ValueCustodyQuote quote) => CanonicalJson.Hash(quote with { QuoteId = string.Empty });

    /// <summary>Checks canonical quote terms before an outer durable admission consumes its expiry.</summary>
    public static bool ValidQuote(ValueCustodyQuote quote)
        => quote.Version == 1 && quote.Request is { } request && Enum.IsDefined(request.Network)
            && !string.IsNullOrWhiteSpace(request.ChainType) && !string.IsNullOrWhiteSpace(request.AssetId)
            && !string.IsNullOrWhiteSpace(request.Sender) && !string.IsNullOrWhiteSpace(request.Recipient)
            && !string.IsNullOrWhiteSpace(request.HoldingAddress) && !string.IsNullOrWhiteSpace(quote.ProtocolIdentity)
            && !string.IsNullOrWhiteSpace(quote.AssetAuthorityHash) && !string.IsNullOrWhiteSpace(quote.NativeCurrency)
            && quote.NativeDecimals is >= 0 and <= 30 && quote.QuotedAt < quote.ExpiresAt
            && UnsignedIntegerText.TryParse(request.QuantityBaseUnits, out var amount) && amount > 0
            && UnsignedIntegerText.TryParse(quote.TransferNetworkFeeBaseUnits, out _)
            && UnsignedIntegerText.TryParse(quote.AcceptanceNetworkFeeBaseUnits, out _)
            && UnsignedIntegerText.TryParse(quote.AcceptanceMinimumBalanceBaseUnits, out _)
            && quote.QuoteId == QuoteId(quote);

    /// <summary>Checks an accepted transfer budget without treating quote expiry as permission to replace wire.</summary>
    public static bool MatchesTransfer(ValueTransferIntent intent)
    {
        if (intent.Cost is not { } cost || !ValidCost(cost) || cost.PayerAddress != intent.Sender) return false;
        var request = cost.Quote.Request;
        return request.ChainType == intent.ChainType && request.Network == intent.Network && request.AssetId == intent.AssetId
            && request.Sender == intent.Sender && request.Recipient == intent.Recipient
            && request.QuantityBaseUnits == intent.QuantityBaseUnits
            && UnsignedIntegerText.TryParse(cost.Quote.TransferNetworkFeeBaseUnits, out var quoted)
            && UnsignedIntegerText.TryParse(cost.MaximumNetworkFeeBaseUnits, out var maximum) && maximum >= quoted;
    }

    /// <summary>Checks that acceptance costs belong to the actual recipient's separate stage.</summary>
    public static bool MatchesAcceptance(ValueAcceptanceIntent intent)
    {
        var cost = intent.Cost;
        if (!ValidCost(cost) || cost.PayerAddress != intent.Recipient || cost.Quote.RecipientReady) return false;
        var request = cost.Quote.Request;
        return request.ChainType == intent.ChainType && request.Network == intent.Network
            && request.AssetId == intent.AssetId && request.Recipient == intent.Recipient
            && UnsignedIntegerText.TryParse(cost.Quote.AcceptanceNetworkFeeBaseUnits, out var quoted)
            && UnsignedIntegerText.TryParse(cost.MaximumNetworkFeeBaseUnits, out var maximum) && maximum >= quoted;
    }

    private static bool ValidCost(ValueTransferCostAuthorization cost)
        => cost.Version == 1 && !string.IsNullOrWhiteSpace(cost.CommandId) && !string.IsNullOrWhiteSpace(cost.Stage)
            && cost.Generation >= 0 && ValidQuote(cost.Quote) && UnsignedIntegerText.TryParse(cost.MaximumNetworkFeeBaseUnits, out _);
}
