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
        // H-2: sensitive entity scopes (Wallet, BlockchainOperation) bind to the authenticated caller, never request.AvatarId.
        var callerAvatarId = GetAvatarIdFromClaims();
        if (callerAvatarId is null) return Unauthorized();

        var result = await _searchManager.SearchAsync(request, callerAvatarId, providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("facets")]
    public async Task<ActionResult<AZOAResult<List<SearchFacet>>>> GetFacets([FromQuery] AZOARequest? providerRequest)
    {
        var result = await _searchManager.GetFacetsAsync(GetAvatarIdFromClaims(), providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// Authenticated caller's avatar id from JWT/api-key claims; null when absent. Matches BlockchainOperationController.GetAvatarIdFromClaims.
    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
