using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Discloses and records the paying participant's exact costs for a recipient-stage Solana NFT step (F-M11).</summary>
[ApiController, Authorize, Route("api/nft-stage-costs/{operationId}")]
public sealed class NftStageCostsController(INftLifecycleService lifecycle, SigningAdmissionAuthorityFactory authority) : ControllerBase
{
    [HttpPost("quote"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Quote(string operationId, [FromBody] NftStageAcceptance stage, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await lifecycle.QuoteStageCostsAsync(operationId, stage, captured.Result, ct);
        return result.IsError || result.Result is null ? BadRequest(result)
            : Ok(AZOAResult<NftProtocolCostOffer>.Success(NftProtocolCostOffer.From(result.Result)));
    }

    [HttpPost("accept"), EnableRateLimiting("financial")]
    public async Task<IActionResult> Accept(string operationId, [FromBody] NftStageCostAcceptance request, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(User, ct);
        if (captured.IsError || captured.Result is null) return BadRequest(captured);
        var result = await lifecycle.AcceptStageCostsAsync(operationId, request, captured.Result, ct);
        return result.IsError ? BadRequest(result) : Accepted(result);
    }
}
