using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface ISearchManager
{
    Task<AZOAResult<SearchResult>> SearchAsync(SearchRequest request, AZOARequest? providerRequest = null);
    Task<AZOAResult<List<SearchFacet>>> GetFacetsAsync(AZOARequest? providerRequest = null);
}
