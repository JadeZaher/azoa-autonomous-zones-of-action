using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Displays the receipt and exact acceptance payload for explicit participant consent.</summary>
public sealed record NftProtocolCostOffer(NftProtocolCostQuote Quote, NftProtocolCostAuthorization Authorization)
{
    public static NftProtocolCostOffer From(NftProtocolCostQuote quote)
        => new(quote, new(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits));
}
