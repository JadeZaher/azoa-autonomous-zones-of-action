using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/operator/action-recovery")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
public sealed class ActionRecoveryOperatorController(ActionOperationRecoveryService recovery) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(AZOAResult<IReadOnlyList<ActionRecoveryView>>.Success(await recovery.ListAsync(limit, ct)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await recovery.GetAsync(id, ct);
        return result.IsError ? NotFound(result) : Ok(result);
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(AZOAResult<object>.Success(await recovery.HistoryAsync(id, ct)));
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, ActionRecoveryRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!AzoaClaims.TryGetSubjectId(User, out var actor)) return Unauthorized();
        var result = await recovery.RetryAsync(id, actor, request, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}
