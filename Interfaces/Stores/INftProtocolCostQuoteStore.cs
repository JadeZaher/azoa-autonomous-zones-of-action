using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Stores private immutable cost offers without admitting execution or granting signing authority.</summary>
public interface INftProtocolCostQuoteStore
{
    /// <summary>Reads a receipt for its exact authenticated actor and tenant, including expired replay evidence.</summary>
    Task<NftProtocolCostQuote?> GetAsync(string quoteId, Guid actor, Guid actingTenantId, CancellationToken ct = default);

    /// <summary>Creates a receipt once or returns an identical replay; conflicting reuse returns null.</summary>
    Task<NftProtocolCostQuote?> ClaimAsync(NftProtocolCostQuote quote, CancellationToken ct = default);
}
