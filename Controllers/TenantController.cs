using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.RateLimiting;

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
    private readonly ITenantCustodialAccountManager _custodialAccounts;

    public TenantController(ITenantManager manager, ITenantCustodialAccountManager custodialAccounts)
    {
        _manager = manager;
        _custodialAccounts = custodialAccounts;
    }

    /// <summary>Reports whether custody, chain, and KYC dependencies are available.</summary>
    [HttpGet("custodial-accounts/capabilities")]
    public async Task<ActionResult<AZOAResult<TenantCustodialCapabilitiesResponse>>> GetCustodialCapabilities()
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();
        return TranslateResult(await _custodialAccounts.GetCapabilitiesAsync(
            tenantId.Value,
            HttpContext.RequestAborted));
    }

    /// <summary>Idempotently ensures one Azoa avatar and platform wallet for an external subject.</summary>
    [HttpPut("custodial-accounts/{externalSubject}")]
    [EnableRateLimiting("tenant-custodial")]
    public async Task<ActionResult<AZOAResult<TenantCustodialAccountStatusResponse>>> EnsureCustodialAccount(
        string externalSubject,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();
        if (!User.HasScope(AzoaScopes.WalletManage) || !User.HasScope(AzoaScopes.KycRead))
            return MissingScope<TenantCustodialAccountStatusResponse>($"{AzoaScopes.WalletManage}, {AzoaScopes.KycRead}");

        var result = await _custodialAccounts.EnsureAsync(
            tenantId.Value,
            externalSubject,
            idempotencyKey ?? string.Empty,
            HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>Returns the secret-free authoritative account, wallet, and KYC status.</summary>
    [HttpGet("custodial-accounts/{externalSubject}")]
    [EnableRateLimiting("tenant-custodial")]
    public async Task<ActionResult<AZOAResult<TenantCustodialAccountStatusResponse>>> GetCustodialAccount(
        string externalSubject)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();
        if (!User.HasScope(AzoaScopes.KycRead))
            return MissingScope<TenantCustodialAccountStatusResponse>(AzoaScopes.KycRead);

        var result = await _custodialAccounts.GetStatusAsync(
            tenantId.Value,
            externalSubject,
            HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>Begins either a hosted or document-reference KYC flow.</summary>
    [HttpPost("custodial-accounts/{externalSubject}/kyc/session")]
    [EnableRateLimiting("tenant-custodial")]
    public async Task<ActionResult<AZOAResult<TenantKycSessionResponse>>> BeginCustodialKyc(
        string externalSubject,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();
        if (!User.HasScope(AzoaScopes.KycSubmit))
            return MissingScope<TenantKycSessionResponse>(AzoaScopes.KycSubmit);

        var result = await _custodialAccounts.BeginKycAsync(
            tenantId.Value,
            externalSubject,
            idempotencyKey ?? string.Empty,
            HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>Submits validated HTTPS document references to Azoa's KYC ledger.</summary>
    [HttpPost("custodial-accounts/{externalSubject}/kyc/submissions")]
    [EnableRateLimiting("tenant-custodial")]
    [RequestSizeLimit(KycDocumentRequestLimits.MaxRequestBodyBytes)]
    public async Task<ActionResult<AZOAResult<TenantKycSubmissionResponse>>> SubmitCustodialKyc(
        string externalSubject,
        [FromBody] TenantKycSubmissionRequest request)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();
        if (!User.HasScope(AzoaScopes.KycSubmit))
            return MissingScope<TenantKycSubmissionResponse>(AzoaScopes.KycSubmit);

        var result = await _custodialAccounts.SubmitKycAsync(
            tenantId.Value,
            externalSubject,
            request,
            HttpContext.RequestAborted);
        return TranslateResult(result);
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
    public ActionResult<AZOAResult<ChildCredentialResponse>> IssueChildCredential(Guid id, [FromBody] IssueChildCredentialModel? model)
    {
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            AZOAResult<ChildCredentialResponse>.Failure(
                "Delegated child credentials are unavailable until every accepting endpoint has an explicit scope policy."));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a manager result to the right HTTP status. A
    /// <see cref="TenantAuthorizationError.NotFound"/>-prefixed message → 404; a
    /// <see cref="TenantAuthorizationError.Forbidden"/>-prefixed message → 403;
    /// retryable in-progress custody/KYC results → 409; any other error → 400.
    /// Cross-tenant / unowned targets are NOT_FOUND by
    /// construction (the manager never emits FORBIDDEN for them), so they 404.
    /// </summary>
    private ActionResult<AZOAResult<T>> TranslateResult<T>(AZOAResult<T> result)
    {
        if (!result.IsError) return Ok(result);

        if (result.Message?.StartsWith(TenantAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        if (result.Message?.StartsWith(TenantAuthorizationError.NotFound, StringComparison.Ordinal) == true)
            return NotFound(result);

        if (result.Message?.StartsWith(
                TenantCustodialOperationError.CustodyInProgress,
                StringComparison.Ordinal) == true
            || result.Message?.StartsWith(
                TenantCustodialOperationError.KycSessionInProgress,
                StringComparison.Ordinal) == true)
        {
            return Conflict(result);
        }

        return BadRequest(result);
    }

    private ActionResult<AZOAResult<T>> MissingScope<T>(string scopes)
        => StatusCode(
            StatusCodes.Status403Forbidden,
            AZOAResult<T>.Failure($"Caller lacks required scope: {scopes}."));

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
