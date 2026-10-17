using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Discloses and records exact costs for proven-no-effect NFT replacement generations.</summary>
[ApiController, Authorize, Route("api/nft-replacement-costs/{operationId}")]
public sealed class NftReplacementCostsController(INftLifecycleService lifecycle, SigningAdmissionAuthorityFactory authority) : ControllerBase
{
    [HttpPost("quote"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Quote(string operationId, [FromBody] NftStageAcceptance stage, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await lifecycle.QuoteReplacementCostsAsync(operationId, stage, captured.Result, ct);
        return result.IsError || result.Result is null ? BadRequest(result)
            : Ok(AZOAResult<NftProtocolCostOffer>.Success(NftProtocolCostOffer.From(result.Result)));
    }

    [HttpPost("accept"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Accept(string operationId, [FromBody] NftReplacementCostAcceptance request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await lifecycle.AcceptReplacementCostsAsync(operationId, request, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Accepted(result);
    }

    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile(string operationId, [FromBody] NftReplacementCostAcceptance request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await lifecycle.ReconcileReplacementCostsAsync(operationId, request, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    [HttpPost("retire"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Retire(string operationId, [FromBody] NftReplacementCostAcceptance request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await lifecycle.RetireReplacementCostsAsync(operationId, request, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Ok(result);
    }
}
