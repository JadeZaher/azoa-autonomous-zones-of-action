using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface INftManager
{
    // callerAvatarId scopes READS to owner-or-public: an NFT (Holon AssetType=="NFT")
    // is returned iff holon.AvatarId == callerAvatarId || holon.IsPublic. Null fails
    // closed (public-only). The caller-supplied query.OwnerAvatarId can only narrow
    // WITHIN the readable set. Single-get of a non-owned/non-private NFT returns a
    // "not found"-style result. See Controllers/AGENTS.md §cross-tenant-read-scope.
    Task<AZOAResult<INft>> GetAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<INft>>> QueryAsync(NftQueryRequest query, Guid? callerAvatarId = null, AZOARequest? request = null);
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
