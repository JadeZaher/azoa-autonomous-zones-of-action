using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly ISearchManager _searchManager;

    public SearchController(ISearchManager searchManager)
    {
        _searchManager = searchManager;
    }

    [HttpPost]
    public async Task<ActionResult<AZOAResult<SearchResult>>> Search([FromBody] SearchRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        var result = await _searchManager.SearchAsync(request, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("facets")]
    public async Task<ActionResult<AZOAResult<List<SearchFacet>>>> GetFacets([FromQuery] AZOARequest? providerRequest)
    {
        var result = await _searchManager.GetFacetsAsync(providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }
}
