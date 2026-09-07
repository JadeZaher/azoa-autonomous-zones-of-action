// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Read-only tax/reporting projection over the caller's own operation history.
/// Both routes are self-scoped; see <c>Services/Analytics/AGENTS.md</c> §self-scope.
/// </summary>
[ApiController]
[Route("api/analytics")]
[Authorize]
public sealed class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsReadService _analytics;

    public AnalyticsController(IAnalyticsReadService analytics)
    {
        _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
    }

    /// <summary>One page of the caller's operations, newest first.</summary>
    [HttpGet("transactions")]
    public async Task<ActionResult<AZOAResult<TransactionHistoryPage>>> GetTransactions(
        [FromQuery] AnalyticsQueryRequest request,
        CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null)
            return Unauthorized();

        var result = await _analytics.GetTransactionsAsync(userId.Value, request, ct);
        return result.IsError ? BadRequestOrUnavailable(result) : Ok(result);
    }

    /// <summary>The caller's net movement per (chain, asset) over the queried window.</summary>
    [HttpGet("positions")]
    public async Task<ActionResult<AZOAResult<PositionsResponse>>> GetPositions(
        [FromQuery] AnalyticsQueryRequest request,
        CancellationToken ct)
    {
        var userId = GetUserIdFromClaims();
        if (userId is null)
            return Unauthorized();

        var result = await _analytics.GetPositionsAsync(userId.Value, request, ct);
        return result.IsError ? BadRequestOrUnavailable(result) : Ok(result);
    }

    /// <summary>A malformed window is the caller's fault; anything else is the node's.</summary>
    private ActionResult BadRequestOrUnavailable<T>(AZOAResult<T> result) =>
        result.Code == AzoaErrorCodes.InvalidRequest
            ? BadRequest(result)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, result);

    /// <summary>
    /// The subject comes only from the credential. There is no <c>userId</c> parameter on either
    /// route — the repo's ActionGraphBundle precedent: an owned-resource read never takes its owner from
    /// the caller. A node-operator cross-user view would be a separate <c>NodeGovern</c> route,
    /// never an optional parameter here.
    /// </summary>
    private Guid? GetUserIdFromClaims()
    {
        var subject = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? User?.FindFirst("sub")?.Value;
        return Guid.TryParse(subject, out var id) ? id : null;
    }
}
