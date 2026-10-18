using AZOA.WebAPI.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Services.NodeAdmin;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/operator/wallet-identities")]
[Authorize(Policy = "RecentNodeOperatorSession")]
public sealed class WalletIdentityOperatorController(WalletAuthIdentityMigrationService migration) : ControllerBase
{
    public sealed record Repair(long Version, string UserId, string ExpectedIdentity, string Reason);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await migration.GetAsync(ct));
    }

    [HttpPost("clear-conflicting-binding")]
    public async Task<IActionResult> Clear(Repair request, CancellationToken ct)
    {
        if (!HttpContext.TryGetOperatorActor(out var operatorActor)) return Unauthorized();
        var actor = operatorActor.SubjectUserId;
        var result = await migration.ClearConflictAsync(actor, request.Version, request.UserId, request.ExpectedIdentity, request.Reason, ct);
        return result.IsError ? Conflict(result) : Ok(result);
    }
}
