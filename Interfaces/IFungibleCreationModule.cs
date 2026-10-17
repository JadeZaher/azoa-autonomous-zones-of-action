using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Creates an asset through one durable signed candidate and authoritative creation evidence.</summary>
public interface IFungibleCreationModule : IBlockchainProviderModule
{
    /// <summary>Quotes exact native costs for the immutable creation intent without signing or sending.</summary>
    Task<AZOAResult<FungibleCreationQuote>> QuoteFungibleCreationAsync(FungibleCreationIntent intent, CancellationToken ct = default);

    /// <summary>Prepares a durably accepted quote; the caller enforces expiry at admission, and recovery retains its original cost limits.</summary>
    Task<AZOAResult<PreparedFungibleCreation>> PrepareFungibleCreationAsync(FungibleCreationIntent intent, CancellationToken ct = default);

    /// <summary>Submits only persisted exact bytes after the caller durably authorizes submission.</summary>
    Task SubmitFungibleCreationAsync(PreparedFungibleCreation prepared, CancellationToken ct = default);

    /// <summary>Returns asset identity only from finalized matching creation evidence; unknown history preserves the candidate.</summary>
    Task<AZOAResult<FungibleCreationObservation>> ObserveFungibleCreationAsync(PreparedFungibleCreation prepared, CancellationToken ct = default);
}
