using System.Security.Claims;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Designer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/action-designer")]
[Authorize(Policy = "DappDevelop")]
public sealed class ActionDesignerController(IWorkflowDesignerModel model, WorkflowDesignerService designer,
    IUserStore users, IApiKeyStore apiKeys) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AZOAResult<WorkflowDesignerAvailability>>> Availability(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var owner = Owner();
        if (owner is null) return Forbid();
        var authority = await TenantAuthority.CheckAsync(owner.Value, users, apiKeys, DateTimeOffset.UtcNow, cancellationToken);
        if (authority.IsError) return StatusCode(503, new AZOAResult<WorkflowDesignerAvailability> { IsError = true, Code = "AI_AUTHORITY_UNAVAILABLE", Message = "Tenant authority could not be checked. Try again later." });
        if (!authority.Result) return StatusCode(403, new AZOAResult<WorkflowDesignerAvailability> { IsError = true, Code = "AI_TENANT_REQUIRED", Message = "This assistant needs your active tenant account and provisioning credential. Ask the node operator to complete tenant setup for your account." });
        return Ok(new AZOAResult<WorkflowDesignerAvailability> { Result = model.Availability with { TenantId = owner } });
    }

    [HttpPost("interview")]
    [RequestSizeLimit(98304)]
    [EnableRateLimiting("workflow-designer")]
    public async Task<ActionResult<AZOAResult<WorkflowDesignerReply>>> Interview(WorkflowDesignerRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var owner = Owner();
        if (owner is null)
            return Unauthorized(new AZOAResult<WorkflowDesignerReply> { IsError = true, Message = "A developer account is required." });
        if (request.TenantId != owner)
            return StatusCode(403, new AZOAResult<WorkflowDesignerReply> { IsError = true, Code = "AI_TENANT_MISMATCH", Message = "Select your own tenant context before sending this interview." });
        if (request.Intake is null || request.Messages is null || request.Messages.Any(m => m is null) ||
            request.ConfirmedResourceHandles is null || request.ConfirmedResourceHandles.Any(handle => string.IsNullOrWhiteSpace(handle) || handle.Length > 200))
            return BadRequest(new AZOAResult<WorkflowDesignerReply> { IsError = true, Message = "An intake and valid interview messages are required." });
        var authority = await TenantAuthority.CheckAsync(owner.Value, users, apiKeys, DateTimeOffset.UtcNow, cancellationToken);
        if (authority.IsError) return StatusCode(503, new AZOAResult<WorkflowDesignerReply> { IsError = true, Code = "AI_AUTHORITY_UNAVAILABLE", Message = "Tenant authority could not be checked. Try again later." });
        if (!authority.Result) return StatusCode(403, new AZOAResult<WorkflowDesignerReply> { IsError = true, Code = "AI_TENANT_REQUIRED", Message = "Your tenant authority is no longer active. Ask the node operator to review tenant setup." });
        var result = await designer.InterviewAsync(request, cancellationToken);
        if (!result.IsError) return Ok(result);
        return StatusCode(result.Code == "AI_CONSENT_REQUIRED" ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable, result);
    }

    private Guid? Owner()
        => !AzoaClaims.IsChildCredential(User) && User.GetActingTenantId() is null &&
            Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var owner) && owner != Guid.Empty
            ? owner : null;
}
