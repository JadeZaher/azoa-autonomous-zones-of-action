using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Services.Dex;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Operator settlement of a parked swap stage from re-proven finalized evidence; never signs or sends.</summary>
[ApiController]
[Route("api/operator/dex")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
public sealed class DexOperatorController(SwapOperationService swaps, ILogger<DexOperatorController> logger) : ControllerBase
{
    [HttpPost("operations/{id}/settle-review")]
    public async Task<IActionResult> SettleReview(string id, [FromBody] SwapOperatorSettlementRequest request, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var actor)) return Unauthorized();
        logger.LogWarning("Operator {OperatorId} settled the parked review of swap {OperationId}", actor, id);
        var settled = await swaps.SettleOperatorReviewAsync(id, request.ExpectedVersion, actor, request.Reason, ct);
        Response.Headers.CacheControl = "no-store";
        return settled.IsError ? Conflict(settled) : Ok(settled);
    }
}
