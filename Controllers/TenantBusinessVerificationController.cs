using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

namespace AZOA.WebAPI.Controllers;

/// <summary>Business verification for the authenticated tenant.</summary>
[ApiController]
[Route("api/tenant/business-verification")]
[Authorize(Policy = "TenantBusinessVerification")]
[EnableRateLimiting("tenant-custodial")]
public sealed class TenantBusinessVerificationController(IKybService service) : ControllerBase
{
    [HttpGet("capabilities")]
    public IActionResult Capabilities() => Ok(AZOAResult<AZOA.WebAPI.Models.HostedSessions.HostedSessionCapabilities>.Success(service.GetCapabilities()));

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (User.FindFirstValue("AuthMethod") == "ApiKey" && !User.HasScope(AzoaScopes.KycRead)) return Forbid();
        if (!TryTenant(out var tenantId)) return Unauthorized();
        return Translate(await service.GetStatusAsync(tenantId, ct));
    }

    [HttpPost("session")]
    public async Task<IActionResult> Begin([FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        if (User.FindFirstValue("AuthMethod") == "ApiKey" && !User.HasScope(AzoaScopes.KycSubmit)) return Forbid();
        if (!TryTenant(out var tenantId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        return Translate(await service.BeginAsync(tenantId, key ?? string.Empty, ct));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (User.FindFirstValue("AuthMethod") == "ApiKey" && !User.HasScope(AzoaScopes.KycRead)) return Forbid();
        if (!TryTenant(out var tenantId)) return Unauthorized();
        return Translate(await service.RefreshAsync(tenantId, ct));
    }

    private bool TryTenant(out Guid tenantId)
        => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out tenantId)
            && tenantId != Guid.Empty;

    private IActionResult Translate<T>(AZOAResult<T> result)
        => result.IsError ? StatusCode(result.Code switch
        {
            AzoaErrorCodes.Forbidden => 403,
            AzoaErrorCodes.Conflict => 409,
            AzoaErrorCodes.DependencyUnavailable => 503,
            _ => 400,
        }, result) : Ok(result);
}
