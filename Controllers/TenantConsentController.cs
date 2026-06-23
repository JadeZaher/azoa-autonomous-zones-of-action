using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// The TENANT-scoped consent surface (tenant-consent-delegation §1, L2). Gated by
/// the <c>TenantScope</c> policy (the same policy <see cref="TenantController"/>
/// uses — it requires the <c>tenant:provision</c> scope, which an API-key tenant
/// principal carries). The tenant id is sourced EXCLUSIVELY from the authenticated
/// key's claim, never a request body.
///
/// This surface is deliberately READ + OFFBOARD-REVOKE ONLY: a tenant CANNOT
/// create a grant here — only the user can grant, authed by the user's own token
/// (H4). A tenant lists ONLY grants made to ITSELF (L2: the manager/store query is
/// tenant-scoped, so a tenant can never receive another tenant's grants) and may
/// revoke a participation it owns on offboard (L3: exact-match within its own
/// grants only).
/// </summary>
[ApiController]
[Route("api/tenant/consent")]
[Authorize(Policy = "TenantScope")]
public class TenantConsentController : ControllerBase
{
    private readonly IConsentManager _manager;

    public TenantConsentController(IConsentManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// List the grants made to this tenant (L2), optionally filtered to a single
    /// participation reference. The list is tenant-scoped at the source, so the
    /// optional in-memory ref filter can only ever narrow THIS tenant's own grants
    /// — it can never surface another tenant's grant even on a colliding ref.
    /// </summary>
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

    /// <summary>
    /// Offboard (L3): revoke the participation grants for this tenant + the given
    /// participation reference (exact-match within the tenant's OWN grants only).
    /// Returns the count revoked. A ref with no matching live grant returns 0 — not
    /// an error.
    /// </summary>
    [HttpDelete("participation/{participationRef}")]
    public async Task<ActionResult<AZOAResult<int>>> RevokeByParticipation(string participationRef)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.RevokeByParticipationAsync(tenantId.Value, participationRef, HttpContext.RequestAborted);
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

    /// <summary>
    /// The tenant id is the authenticated key's owner avatar id — ALWAYS from the
    /// claim, never from a request body. Mirrors
    /// <c>TenantController.GetTenantIdFromClaims</c>.
    /// </summary>
    private Guid? GetTenantIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
