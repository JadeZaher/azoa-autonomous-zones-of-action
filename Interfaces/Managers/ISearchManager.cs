using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface ISearchManager
{
    /// <paramref name="callerUserId"/> scopes sensitive entity types (Wallet, BlockchainOperation) to the authenticated caller; null = fail-closed (no cross-tenant enumeration).
    Task<AZOAResult<SearchResult>> SearchAsync(SearchRequest request, Guid? callerUserId, AZOARequest? providerRequest = null);
    /// <paramref name="callerUserId"/> scopes Holon/Wallet/ActionGraphBundle counts to caller-owned-or-public; null = fail-closed.
    Task<AZOAResult<List<SearchFacet>>> GetFacetsAsync(Guid? callerUserId, AZOARequest? providerRequest = null);
}
