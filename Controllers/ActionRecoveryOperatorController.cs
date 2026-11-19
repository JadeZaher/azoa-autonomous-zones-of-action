using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/operator/action-recovery")]
[Authorize(Policy = "RecentNodeOperatorSession")]
[EnableRateLimiting("financial")]
public sealed class ActionRecoveryOperatorController(ActionOperationRecoveryService recovery, IActionManager actionManager) : ControllerBase
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
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var actor = operatorActor.SubjectUserId;
        var result = await recovery.RetryAsync(id, actor, request, ct, operatorActor);
        return result.IsError ? Conflict(result) : Ok(result);
    }

    [HttpGet("publish-policy")]
    public IActionResult GetPublishPolicy()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(AZOAResult<ActionPublishApprovalPolicyView>.Success(actionManager.GetPublishApprovalPolicy()));
    }

    [HttpPut("publish-policy")]
    public IActionResult UpdatePublishPolicy([FromBody] ActionPublishApprovalPolicyUpdateRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var updated = actionManager.SetPublishApprovalPolicy(request.RequireOperatorApprovalForPublish, operatorActor.AuditActor);
        return Ok(AZOAResult<ActionPublishApprovalPolicyView>.Success(updated));
    }

    [HttpGet("publish-approvals")]
    public async Task<IActionResult> ListPublishApprovals([FromQuery] string? status = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await actionManager.ListPublishApprovalsAsync(status, limit, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("publish-approvals/{id:guid}/decision")]
    public async Task<IActionResult> DecidePublishApproval(Guid id, [FromBody] ActionPublishApprovalDecisionRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var result = await actionManager.DecidePublishApprovalAsync(
            id, request.Approve, request.Reason, operatorActor.SubjectUserId, operatorActor.AuditActor, ct);
        if (!result.IsError) return Ok(result);
        return result.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || result.Message.Contains("PendingApproval", StringComparison.OrdinalIgnoreCase)
            ? Conflict(result)
            : BadRequest(result);
    }
}
