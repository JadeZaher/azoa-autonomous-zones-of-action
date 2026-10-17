using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Reads provider-owned NFT creation costs without custody, signing or submission.</summary>
public interface INftProtocolCostModule : IBlockchainProviderModule
{
    /// <summary>Quotes the exact creation intent and independently pinned execution policy.</summary>
    Task<AZOAResult<NftProtocolCostEstimate>> QuoteNftProtocolCostsAsync(NftTransactionIntent intent, CancellationToken ct = default);
}
