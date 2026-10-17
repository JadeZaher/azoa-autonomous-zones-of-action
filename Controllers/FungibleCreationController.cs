using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

/// <summary>Participant-scoped cost consent and recovery for durable fungible creation.</summary>
[ApiController, Authorize, Route("api/fungible-creation")]
public sealed class FungibleCreationController(IFungibleCreationService service,
    AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory signingAuthority) : ControllerBase
{
    [HttpPost("{id}/replace")]
    public async Task<IActionResult> Replace(string id, [FromBody] FungibleContinuationRequest request)
    {
        if (Actor() is null) return Unauthorized();
        var credential = await signingAuthority.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        return Render(await service.ReplaceAsync(id, request, credential.Result, HttpContext.RequestAborted));
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, [FromBody] FungibleContinuationRequest request)
    {
        if (Actor() is null) return Unauthorized();
        var credential = await signingAuthority.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        return Render(await service.CancelAsync(id, request, credential.Result, HttpContext.RequestAborted));
    }

    [HttpGet("{id}/attachment")]
    public async Task<IActionResult> Attachment(string id)
    {
        if (Actor() is not { } actor) return Unauthorized();
        var credential = await signingAuthority.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        var consent = await signingAuthority.BindConsentAsync(credential.Result, [AzoaScopes.DappDevelop], HttpContext.RequestAborted);
        if (consent.IsError || consent.Result is null) return BadRequest(consent);
        var result = await service.GetAttachmentAsync(id, actor, User.GetActingTenantId(), HttpContext.RequestAborted);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("{id}/attachment")]
    public async Task<IActionResult> Attach(string id, [FromBody] FungibleAttachmentRequest request)
    {
        if (Actor() is null) return Unauthorized();
        var credential = await signingAuthority.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (credential.IsError || credential.Result is null) return BadRequest(credential);
        var result = await service.AttachAsync(id, request, credential.Result, HttpContext.RequestAborted);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25)
        => Actor() is { } actor ? Ok(await service.ListAsync(actor, User.GetActingTenantId(), offset, limit, HttpContext.RequestAborted)) : Unauthorized();

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
        => Actor() is { } actor ? Render(await service.GetAsync(id, actor, User.GetActingTenantId(), HttpContext.RequestAborted)) : Unauthorized();

    [HttpPost("{id}/quote")]
    public async Task<IActionResult> Quote(string id)
    {
        if (Actor() is not { } actor) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.TokenCreateSign)) return Forbid();
        return Render(await service.OfferQuoteAsync(id, actor, User.GetActingTenantId(), HttpContext.RequestAborted));
    }

    [HttpPost("{id}/accept-quote")]
    public async Task<IActionResult> Accept(string id, [FromBody] FungibleQuoteAcceptance request)
    {
        if (Actor() is not { } actor) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.TokenCreateSign)) return Forbid();
        return Render(await service.AcceptQuoteAsync(id, actor, User.GetActingTenantId(), request.Version, request.QuoteId, HttpContext.RequestAborted));
    }

    [HttpPost("{id}/continue")]
    public async Task<IActionResult> Continue(string id, [FromBody] FungibleContinuationRequest request)
    {
        if (Actor() is not { } actor) return Unauthorized();
        if (!User.HasSigningScope(AzoaScopes.TokenCreateSign)) return Forbid();
        var captured = await signingAuthority.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await service.ContinueAsync(id, actor, User.GetActingTenantId(), HttpContext.RequestAborted, captured.Result, request));
    }

    [HttpPost("{id}/refresh")]
    public async Task<IActionResult> Refresh(string id)
        => Actor() is { } actor ? Render(await service.RefreshAsync(id, actor, User.GetActingTenantId(), HttpContext.RequestAborted)) : Unauthorized();

    private Guid? Actor()
        => Guid.TryParse(User.FindFirstValue("UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) && id != Guid.Empty ? id : null;

    private IActionResult Render(AZOAResult<FungibleCreationView> result)
        => result.IsError ? BadRequest(result) : result.Result?.State is "Completed" or "Cancelled" or "FailedNoEffect" ? Ok(result) : Accepted(result);
}
