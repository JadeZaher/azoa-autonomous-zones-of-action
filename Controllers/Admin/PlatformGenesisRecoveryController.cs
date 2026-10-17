using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers.Admin;

/// <summary>Explicit operator consent and recovery for the original platform genesis operation.</summary>
[ApiController, Authorize(Policy = "Operator"), Route("api/admin/platform-token/creation/{id}")]
public sealed class PlatformGenesisRecoveryController(IFungibleCreationStore store, IFungibleCreationService lifecycle,
    SigningAdmissionAuthorityFactory credentials) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string id)
        => await OwnsGenesisAsync(id) is { } actor ? Render(await lifecycle.GetAsync(id, actor, null, HttpContext.RequestAborted)) : NotFound();

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(string id)
        => await OwnsGenesisAsync(id) is { } actor ? Render(await lifecycle.RefreshAsync(id, actor, null, HttpContext.RequestAborted)) : NotFound();

    [HttpPost("quote"), Authorize(Policy = "RecentNodeOperatorSession")]
    public async Task<IActionResult> Quote(string id)
        => await OwnsGenesisAsync(id) is { } actor ? Render(await lifecycle.OfferQuoteAsync(id, actor, null, HttpContext.RequestAborted)) : NotFound();

    [HttpPost("accept-quote"), Authorize(Policy = "RecentNodeOperatorSession")]
    public async Task<IActionResult> Accept(string id, [FromBody] FungibleQuoteAcceptance request)
        => await OwnsGenesisAsync(id) is { } actor
            ? Render(await lifecycle.AcceptQuoteAsync(id, actor, null, request.Version, request.QuoteId, HttpContext.RequestAborted)) : NotFound();

    [HttpPost("continue"), Authorize(Policy = "RecentNodeOperatorSession")]
    public async Task<IActionResult> Continue(string id, [FromBody] FungibleContinuationRequest request)
    {
        if (await OwnsGenesisAsync(id) is not { } actor) return NotFound();
        var captured = await credentials.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await lifecycle.ContinueAsync(id, actor, null, HttpContext.RequestAborted, captured.Result, request));
    }

    [HttpPost("replace"), Authorize(Policy = "RecentNodeOperatorSession")]
    public async Task<IActionResult> Replace(string id, [FromBody] FungibleContinuationRequest request)
    {
        if (await OwnsGenesisAsync(id) is null) return NotFound();
        var captured = await credentials.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        return Render(await lifecycle.ReplaceAsync(id, request, captured.Result, HttpContext.RequestAborted));
    }

    private async Task<Guid?> OwnsGenesisAsync(string id)
    {
        if (!AzoaClaims.IsNodeOperator(User) || !AzoaClaims.TryGetSubjectId(User, out var actor) || actor != NodeOperatorIdentity.UserId
            || User.GetActingTenantId() is not null) return null;
        var row = await store.GetAsync(id, HttpContext.RequestAborted);
        return row?.ActorUserId == actor.ToString("N") && row.TenantId is null
            && JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)?.GenesisAuthority is not null ? actor : null;
    }

    private IActionResult Render(AZOAResult<FungibleCreationView> result)
        => result.IsError ? BadRequest(result) : result.Result?.State is "Completed" or "Cancelled" or "FailedNoEffect" ? Ok(result) : Accepted(result);
}
