using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Services.Dex;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.NodeAdmin;

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
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var actor = operatorActor.SubjectUserId;
        var settled = await swaps.SettleOperatorReviewAsync(id, request.ExpectedVersion, actor, request.Reason, ct);
        if (settled.IsError)
            logger.LogWarning("Operator {OperatorId} settle-review of swap {OperationId} was refused: {Reason}", operatorActor.AuditActor, id, settled.Message);
        else
            logger.LogWarning("Operator {OperatorId} settled the parked review of swap {OperationId}", operatorActor.AuditActor, id);
        Response.Headers.CacheControl = "no-store";
        return settled.IsError ? Conflict(settled) : Ok(settled);
    }
}
