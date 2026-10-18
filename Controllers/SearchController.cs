using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.NodeAdmin;

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

        // Discoverable gate always applies here (owner decision 2026-10-18): this plain
        // [Authorize] route can never carry a node-operator session in the first place —
        // see OperatorSearch below and Controllers/AGENTS.md §User responses.
        var result = await _searchManager.SearchAsync(request, callerUserId, providerRequest, isNodeAdmin: false);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Security-review fix (owner correction 2026-10-18): the plain <see cref="Search"/>
    /// route's bare <c>[Authorize]</c> is NOT in the fixed policy allowlist
    /// (<c>NodeOperatorSession</c>/<c>RecentNodeOperatorSession</c>/<c>DappRoleAssignment</c>/
    /// <c>Operator</c>/<c>NodeGovern</c>) that <c>Program.cs</c>'s <c>OnTokenValidated</c> accepts
    /// a node-operator session token on — a node-operator bearer hitting <c>POST /api/search</c>
    /// is rejected at token validation (401) before this controller ever runs. "Node admins will
    /// always be able to find users" is therefore served by this dedicated <c>Operator</c>-policy
    /// route instead of a claims check inside <see cref="Search"/>: every real node-operator
    /// session token already carries <c>scope=operator</c> and no acting tenant
    /// (<c>NodeOperatorManager.CreateToken</c>), so the policy is exactly the reachable set.
    /// See Controllers/AGENTS.md §User responses.
    /// </summary>
    [HttpPost("operator")]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<AZOAResult<SearchResult>>> OperatorSearch([FromBody] SearchRequest request, [FromQuery] AZOARequest? providerRequest)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();

        var result = await _searchManager.SearchAsync(request, operatorActor.SubjectUserId, providerRequest, isNodeAdmin: true);
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
}
