using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Consent;

namespace AZOA.WebAPI.Controllers;

/// <summary>Tenant-scoped consent reads and atomic membership closure; see Controllers/AGENTS.md.</summary>
[ApiController]
[Route("api/tenant/consent")]
[Authorize(Policy = "TenantScope")]
public class TenantConsentController : ControllerBase
{
    private readonly IConsentManager _manager;

    private readonly ConsentLifecycleService _lifecycle;

    public TenantConsentController(IConsentManager manager, ConsentLifecycleService lifecycle)
    {
        _manager = manager;
        _lifecycle = lifecycle;
    }

    /// <summary>Lists only grants to the authenticated tenant, optionally narrowed by participation reference.</summary>
    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<ConsentGrantResponse>>>> List([FromQuery] string? participationRef)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.ListForTenantAsync(tenantId.Value, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);

        var grants = result.Result ?? Enumerable.Empty<ConsentGrant>();
        if (!string.IsNullOrWhiteSpace(participationRef))
        {
            var filter = participationRef.Trim();
            grants = grants.Where(g => string.Equals(g.ParticipationRef, filter, StringComparison.Ordinal));
        }

        return Ok(new AZOAResult<IEnumerable<ConsentGrantResponse>>
        {
            Result = grants.Select(ToResponse).ToList(),
            Message = result.Message,
        });
    }

    /// <summary>Closes the exact participation membership and returns its original closure receipt.</summary>
    [HttpDelete("participation/{participationRef}")]
    public async Task<ActionResult<AZOAResult<ConsentOffboardReceipt>>> RevokeByParticipation(string participationRef)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _lifecycle.OffboardAsync(User, participationRef, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);

        return Ok(result);
    }

    /// <summary>Closes provisioned participation using the authenticated tenant and external user reference.</summary>
    [HttpDelete("provisioned")]
    public async Task<ActionResult<AZOAResult<ConsentOffboardReceipt>>> RevokeProvisioned([FromQuery] string externalUserId)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(externalUserId))
            return BadRequest(AZOAResult<ConsentOffboardReceipt>.FailureWithCode(
                "externalUserId is required.", AzoaErrorCodes.InvalidRequest));

        var participationRef = TenantProvisionedGrant.ParticipationRefFor(tenantId.Value, externalUserId);

        var result = await _lifecycle.OffboardAsync(User, participationRef, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);

        return Ok(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ConsentGrantResponse ToResponse(ConsentGrant g) => new()
    {
        GrantId = g.Id,
        TenantId = g.TenantId,
        Scopes = g.Scopes,
        Origin = g.Origin.ToString(),
        ParticipationRef = g.ParticipationRef,
        GrantedAt = g.GrantedAt,
        ExpiresAt = g.ExpiresAt,
        RevokedAt = g.RevokedAt,
    };

    /// <summary>Reads the authenticated tenant subject identifier.</summary>
    private Guid? GetTenantIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
