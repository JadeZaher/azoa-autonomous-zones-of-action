using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Discloses and manages exact owner-authorized delayed custody releases.</summary>
[ApiController, Authorize, Route("api/hold-release-mandates")]
public sealed class HoldReleaseMandatesController(HoldReleaseMandateService mandates, SigningAdmissionAuthorityFactory authority) : ControllerBase
{
    [HttpPost("offers"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Offer([FromBody] HoldReleaseMandateOfferRequest request, CancellationToken ct)
    {
        var credential = await authority.CaptureCredentialAsync(User, ct);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        var result = await mandates.OfferAsync(request, credential.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("accept"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Accept([FromBody] HoldReleaseMandateAcceptance request, CancellationToken ct)
    {
        var credential = await authority.CaptureCredentialAsync(User, ct);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        var result = await mandates.AcceptAsync(request, credential.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken ct = default)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var result = await mandates.ListAsync(actor, User.GetActingTenantId(), offset, limit, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var result = await mandates.GetAsync(id, actor, User.GetActingTenantId(), ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{id}/reauthorize"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Reauthorize(string id, [FromBody] HoldRecoveryRequest request, CancellationToken ct)
    {
        var credential = await authority.CaptureCredentialAsync(User, ct);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        var result = await mandates.ReauthorizeAsync(id, request, credential.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{id}/revoke"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Revoke(string id, [FromBody] HoldRecoveryRequest request, CancellationToken ct)
    {
        var credential = await authority.CaptureCredentialAsync(User, ct);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        var result = await mandates.RevokeAsync(id, request, credential.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{id}/reconcile-request")]
    public async Task<IActionResult> ReconcileRequest(string id, [FromBody] HoldMandateRecoveryLookup request, CancellationToken ct)
    {
        var credential = await authority.CaptureCredentialAsync(User, ct);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        var result = await mandates.ReconcileRecoveryAsync(id, request, credential.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    private Guid? Actor() => Guid.TryParse(User.FindFirstValue("UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub"), out var actor) && actor != Guid.Empty ? actor : null;
}
