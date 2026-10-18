using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.NodeAdmin;

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
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var operatorUserId = operatorActor.SubjectUserId;
        logger.LogWarning("Operator {OperatorId} resolved deployment {OperationId} with {Action}", operatorActor.AuditActor, id, request?.Action);
        var result = await deployments.ResolveNeedsOperatorAsync(id, operatorUserId, request!, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}
