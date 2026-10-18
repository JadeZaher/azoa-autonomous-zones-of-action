using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
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
        // H-2: sensitive entity scopes (Wallet, BlockchainOperation) bind to the authenticated caller, never request.UserId.
        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null) return Unauthorized();

        var result = await _searchManager.SearchAsync(request, callerUserId, providerRequest, IsNodeAdmin());
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("facets")]
    public async Task<ActionResult<AZOAResult<List<SearchFacet>>>> GetFacets([FromQuery] AZOARequest? providerRequest)
    {
        var result = await _searchManager.GetFacetsAsync(GetUserIdFromClaims(), providerRequest);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// Authenticated caller's user id from JWT/api-key claims; null when absent. Matches BlockchainOperationController.GetUserIdFromClaims.
    private Guid? GetUserIdFromClaims()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>
    /// Owner correction 2026-10-18 (user-discoverable): "node admins will always be able to
    /// find users." Deliberately NOT the "Operator" authorization policy — that policy also
    /// requires the step-up <c>operator</c> scope and no acting tenant (Program.cs), which a
    /// node admin searching from an ordinary dashboard JWT does not carry. This bypass is
    /// axis-1 only: a dedicated node-operator session (<see cref="AzoaClaims.IsNodeOperator"/>)
    /// that is not API-key authenticated (<see cref="ClaimsPrincipalExtensions.IsApiKey"/>,
    /// the same axis-1 rule the "Operator" policy applies first) — scope and acting-tenant
    /// do not matter here. See Controllers/AGENTS.md §User responses.
    /// </summary>
    private bool IsNodeAdmin() => AzoaClaims.IsNodeOperator(User) && !User.IsApiKey();
}
