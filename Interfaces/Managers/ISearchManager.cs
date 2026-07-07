using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface ISearchManager
{
    /// <paramref name="callerAvatarId"/> scopes sensitive entity types (Wallet, BlockchainOperation) to the authenticated caller; null = fail-closed (no cross-tenant enumeration).
    Task<AZOAResult<SearchResult>> SearchAsync(SearchRequest request, Guid? callerAvatarId, AZOARequest? providerRequest = null);
    /// <paramref name="callerAvatarId"/> scopes Holon/Wallet/STARODK counts to caller-owned-or-public; null = fail-closed.
    Task<AZOAResult<List<SearchFacet>>> GetFacetsAsync(Guid? callerAvatarId, AZOARequest? providerRequest = null);
}
