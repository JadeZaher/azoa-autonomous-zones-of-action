using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Tenant provisioning surface (tenant-onboarding). Every action requires the
/// <c>tenant:provision</c> scope via the <c>TenantScope</c> policy. The tenant id
/// is sourced exclusively from the authenticated key's claim — never from a
/// request body (IDOR rule). Cross-tenant / unowned targets return 404 (not 403)
/// so a prober cannot enumerate another tenant's avatars (isolation crux, B5).
/// </summary>
[ApiController]
[Route("api/tenant")]
[Authorize(Policy = "TenantScope")]
public class TenantController : ControllerBase
{
    private readonly ITenantManager _manager;

    public TenantController(ITenantManager manager)
    {
        _manager = manager;
    }

    /// <summary>Provision a new child avatar under the authenticated tenant.</summary>
    [HttpPost("avatars")]
    public async Task<ActionResult<AZOAResult<ChildAvatarResponse>>> ProvisionChild([FromBody] ProvisionChildModel model)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.ProvisionChildAsync(tenantId.Value, model, HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>List the tenant's child avatars (optionally filtered by external user id).</summary>
    [HttpGet("avatars")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ChildAvatarResponse>>>> ListChildren([FromQuery] string? externalUserId)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.ListChildrenAsync(tenantId.Value, externalUserId, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Resolve one child by the tenant's own external user id.</summary>
    [HttpGet("avatars/{externalUserId}")]
    public async Task<ActionResult<AZOAResult<ChildAvatarResponse>>> ResolveChild(string externalUserId)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.ResolveChildAsync(tenantId.Value, externalUserId, HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>Issue a short-lived child-scoped credential to act as that child.</summary>
    [HttpPost("avatars/{id:guid}/credential")]
    public async Task<ActionResult<AZOAResult<ChildCredentialResponse>>> IssueChildCredential(Guid id, [FromBody] IssueChildCredentialModel? model)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var requested = model?.Scopes ?? new List<string>();
        var tenantScopes = User.GetScopes();

        var result = await _manager.IssueChildCredentialAsync(
            tenantId.Value, id, requested, tenantScopes, HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a manager result to the right HTTP status. A
    /// <see cref="TenantAuthorizationError.NotFound"/>-prefixed message → 404; a
    /// <see cref="TenantAuthorizationError.Forbidden"/>-prefixed message → 403;
    /// any other error → 400. Cross-tenant / unowned targets are NOT_FOUND by
    /// construction (the manager never emits FORBIDDEN for them), so they 404.
    /// </summary>
    private ActionResult<AZOAResult<T>> TranslateResult<T>(AZOAResult<T> result)
    {
        if (!result.IsError) return Ok(result);

        if (result.Message?.StartsWith(TenantAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        if (result.Message?.StartsWith(TenantAuthorizationError.NotFound, StringComparison.Ordinal) == true)
            return NotFound(result);

        return BadRequest(result);
    }

    /// <summary>
    /// The tenant id is the authenticated key's owner avatar id — ALWAYS from the
    /// claim, never from a request body. Mirrors
    /// <c>STARODKController.GetAvatarIdFromClaims</c>.
    /// </summary>
    private Guid? GetTenantIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
