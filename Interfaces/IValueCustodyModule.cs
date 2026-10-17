using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Provides current custody evidence, explicit native budgets and durable recipient acceptance.</summary>
public interface IValueCustodyModule : IBlockchainProviderModule
{
    /// <summary>Reads current asset/account mechanics and prices both participants' distinct stages without signing.</summary>
    Task<AZOAResult<ValueCustodyQuote>> QuoteValueCustodyAsync(ValueCustodyQuoteRequest request, CancellationToken ct);

    /// <summary>Journals and signs only the actual recipient's explicitly authorized acceptance without sending.</summary>
    Task<AZOAResult<PreparedValueAcceptance>> PrepareValueAcceptanceAsync(ValueAcceptanceIntent intent, CancellationToken ct);

    /// <summary>Rebroadcasts only the exact journaled acceptance bytes after outer first-send authorization.</summary>
    Task SubmitValueAcceptanceAsync(PreparedValueAcceptance prepared, CancellationToken ct);

    /// <summary>Observes exact bound acceptance evidence without preparing or obtaining new consent.</summary>
    Task<AZOAResult<ChainConfirmation>> ObserveValueAcceptanceAsync(PreparedValueAcceptance prepared, CancellationToken ct);
}
