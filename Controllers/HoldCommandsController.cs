using System.Security.Claims;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Authenticated hold quote, participant consent and exact-operation recovery.</summary>
[ApiController, Authorize, Route("api/hold-commands")]
public sealed class HoldCommandsController(HoldCommandService commands, SigningAdmissionAuthorityFactory authority) : ControllerBase
{
    [HttpPost, EnableRateLimiting("financial")]
    public async Task<IActionResult> Start([FromBody] HoldStartRequest request, CancellationToken ct)
    {
        if (request?.Intent is null) return BadRequest("An exact hold command is required.");
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.AdmitAsync(request.Intent, request.SenderWalletId, request.RecipientWalletId, captured.Result, ct));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken ct = default)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var result = await commands.ListAsync(actor, User.GetActingTenantId(), offset, limit, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
        => Actor() is { } actor ? Render(await commands.GetAsync(id, actor, User.GetActingTenantId(), ct)) : Unauthorized();

    [HttpPost("{id}/refresh")]
    public async Task<IActionResult> Refresh(string id, CancellationToken ct)
        => Actor() is { } actor ? Render(await commands.RefreshAsync(id, actor, User.GetActingTenantId(), ct)) : Unauthorized();

    [HttpPost("{id}/reconcile-request")]
    public async Task<IActionResult> ReconcileRequest(string id, [FromBody] HoldCommandRequestLookup request, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var result = await commands.ReconcileRequestAsync(id, request, actor, User.GetActingTenantId(), ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{id}/continue"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Continue(string id, [FromBody] HoldContinuationRequest request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.ContinueAsync(id, request, captured.Result, ct));
    }

    [HttpPost("{id}/accept"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Accept(string id, [FromBody] HoldContinuationRequest request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.AcceptAsync(id, request, captured.Result, ct));
    }

    [HttpPost("{id}/accept-opening"), EnableRateLimiting("financial")]
    public async Task<IActionResult> AcceptOpening(string id, [FromBody] HoldWorkflowOpeningAcceptance request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.AcceptWorkflowOpeningAsync(id, request, captured.Result, ct));
    }

    [HttpPost("{id}/reconcile-opening")]
    public async Task<IActionResult> ReconcileOpening(string id, [FromBody] HoldWorkflowOpeningAcceptance request, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var result = await commands.ReconcileWorkflowOpeningAsync(id, request, actor, User.GetActingTenantId(), ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{id}/replace-opening"), EnableRateLimiting("financial")]
    public async Task<IActionResult> ReplaceOpening(string id, [FromBody] HoldRecoveryRequest request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.ReplaceWorkflowOpeningAsync(id, request, captured.Result, ct));
    }

    [HttpPost("{id}/reconcile-opening-recovery")]
    public async Task<IActionResult> ReconcileOpeningRecovery(string id, [FromBody] HoldWorkflowOpeningRecoveryLookup request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await commands.ReconcileWorkflowRecoveryAsync(id, request, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{id}/withdraw-opening"), EnableRateLimiting("financial")]
    public async Task<IActionResult> WithdrawOpening(string id, [FromBody] HoldRecoveryRequest request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.WithdrawWorkflowOpeningAsync(id, request, captured.Result, ct));
    }

    [HttpPost("{id}/replace"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Replace(string id, [FromBody] HoldRecoveryRequest request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.ReplaceAsync(id, request, captured.Result, ct));
    }

    [HttpPost("{id}/cancel"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Cancel(string id, [FromBody] HoldRecoveryRequest request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await commands.CancelAsync(id, request, captured.Result, ct));
    }

    private Guid? Actor() => Guid.TryParse(User.FindFirstValue("UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub"), out var id) && id != Guid.Empty ? id : null;

    private IActionResult Render(AZOAResult<HoldCommandView> result)
        => result.IsError ? BadRequest(result) : result.Result?.State is "Completed" or "Cancelled" or "FailedNoEffect" ? Ok(result) : Accepted(result);
}
