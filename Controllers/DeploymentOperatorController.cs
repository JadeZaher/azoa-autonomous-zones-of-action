using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Node-operator exit for native deployments parked in NeedsOperator; see Services/Deployments/AGENTS.md §Operator resolution.</summary>
[ApiController]
[Route("api/operator/deployments")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
public sealed class DeploymentOperatorController(IDeploymentService deployments, ILogger<DeploymentOperatorController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(AZOAResult<IReadOnlyList<DeploymentReceipt>>.Success(await deployments.ListNeedsOperatorAsync(limit, ct)));
    }

    [HttpPost("{id}/resolve")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Resolve(string id, DeploymentOperatorResolution request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!AzoaClaims.TryGetSubjectId(User, out var operatorUserId)) return Unauthorized();
        logger.LogWarning("Operator {OperatorId} resolved deployment {OperationId} with {Action}", operatorUserId, id, request?.Action);
        var result = await deployments.ResolveNeedsOperatorAsync(id, operatorUserId, request!, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}
