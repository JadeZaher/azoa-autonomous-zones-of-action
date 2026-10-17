using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Discloses, accepts and reconciles costs for an original frozen NFT workflow execution.</summary>
[ApiController, Authorize, Route("api/nft-workflow-costs")]
public sealed class NftWorkflowCostsController(NftWorkflowCostConsentService costs,
    SigningAdmissionAuthorityFactory authority) : ControllerBase
{
    [HttpPost("{executionId:guid}/quote"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Quote(Guid executionId, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await costs.QuoteAsync(executionId, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("accept"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Accept([FromBody] NftWorkflowCostAcceptance request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await costs.AcceptAsync(request, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Accepted(result);
    }

    [HttpPost("retire"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Retire([FromBody] NftWorkflowCostAcceptance request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await costs.RetireAsync(request, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile([FromBody] NftWorkflowCostAcceptance request, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var actor) || actor == Guid.Empty) return Unauthorized();
        var result = await costs.ReconcileAsync(request, actor, User.GetActingTenantId(), ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }
}
