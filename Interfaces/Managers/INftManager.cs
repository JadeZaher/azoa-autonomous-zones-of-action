using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface INftManager
{
    Task<AZOAResult<INft>> GetAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<INft>>> QueryAsync(NftQueryRequest query, AZOARequest? request = null);
    // tenant-consent-delegation AC4/AC4b: an optional actingTenantId stamps the
    // tenant that drove a quest Tier-2 node onto the produced BlockchainOperation
    // (along with the signing scope) so the custody seam's live consent gate fires.
    // Null (the default) = user-driven / direct caller — no op-level tenant stamp,
    // identical behaviour to before.
    Task<AZOAResult<IBlockchainOperation>> MintAsync(NftMintRequest request, Guid avatarId, AZOARequest? providerRequest = null, Guid? actingTenantId = null);
    Task<AZOAResult<IBlockchainOperation>> TransferAsync(Guid nftId, NftTransferRequest request, Guid avatarId, AZOARequest? providerRequest = null, Guid? actingTenantId = null);
    Task<AZOAResult<IBlockchainOperation>> BurnAsync(Guid nftId, Guid walletId, Guid avatarId, AZOARequest? providerRequest = null);
    Task<AZOAResult<NftMetadata>> GetMetadataAsync(Guid id, AZOARequest? request = null);
}
