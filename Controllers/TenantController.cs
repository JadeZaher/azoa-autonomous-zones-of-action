using AZOA.WebAPI.Services.Consent;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Tenant provisioning surface (tenant-onboarding). Every action requires the
/// <c>tenant:provision</c> scope via the <c>TenantScope</c> policy. The tenant id
/// is sourced exclusively from the authenticated key's claim — never from a
/// request body (IDOR rule). Cross-tenant / unowned targets return 404 (not 403)
/// so a prober cannot enumerate another tenant's users (isolation crux, B5).
/// </summary>
[ApiController]
[Route("api/tenant")]
[Authorize(Policy = "TenantScope")]
public class TenantController : ControllerBase
{
    private readonly ITenantManager _manager;
    private readonly ITenantCustodialAccountManager _custodialAccounts;

    private readonly SigningAdmissionAuthorityFactory _authorities;

    public TenantController(ITenantManager manager, ITenantCustodialAccountManager custodialAccounts, SigningAdmissionAuthorityFactory authorities)
    {
        _manager = manager;
        _custodialAccounts = custodialAccounts;
        _authorities = authorities;
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

    /// <summary>Idempotently ensures one Azoa user and platform wallet for an external subject.</summary>
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

        var credential = await _authorities.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (credential.IsError || credential.Result is null) return Unauthorized();
        var authority = await _authorities.BindConsentAsync(credential.Result, [AzoaScopes.TenantProvision, AzoaScopes.WalletManage, AzoaScopes.KycRead], HttpContext.RequestAborted);
        if (authority.IsError || authority.Result is null) return Forbid();

        var result = await _custodialAccounts.EnsureAsync(
            tenantId.Value,
            externalSubject,
            idempotencyKey ?? string.Empty,
            authority.Result,
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

        var authority = await CustodialAuthorityAsync(AzoaScopes.KycRead);
        if (authority is null) return Forbid();
        var result = await _custodialAccounts.GetStatusAsync(
            tenantId.Value,
            externalSubject,
            authority,
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

        var authority = await CustodialAuthorityAsync(AzoaScopes.KycSubmit);
        if (authority is null) return Forbid();
        var result = await _custodialAccounts.BeginKycAsync(
            tenantId.Value,
            externalSubject,
            idempotencyKey ?? string.Empty,
            authority,
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

        var authority = await CustodialAuthorityAsync(AzoaScopes.KycSubmit);
        if (authority is null) return Forbid();
        var result = await _custodialAccounts.SubmitKycAsync(
            tenantId.Value,
            externalSubject,
            request,
            authority,
            HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>Provision a new child user under the authenticated tenant.</summary>
    [HttpPost("users")]
    public async Task<ActionResult<AZOAResult<ChildUserResponse>>> ProvisionChild([FromBody] ProvisionChildModel model)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var credential = await _authorities.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (credential.IsError || credential.Result is null) return Unauthorized();
        var authority = await _authorities.BindConsentAsync(credential.Result, [AzoaScopes.TenantProvision], HttpContext.RequestAborted);
        if (authority.IsError || authority.Result is null) return Forbid();

        var result = await _manager.ProvisionChildAsync(tenantId.Value, model, authority.Result, HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    private async Task<SigningConsentWitness?> CustodialAuthorityAsync(string scope)
    {
        var credential = await _authorities.CaptureCredentialAsync(User, HttpContext.RequestAborted);
        if (credential.IsError || credential.Result is null) return null;
        var authority = await _authorities.BindConsentAsync(credential.Result, [AzoaScopes.TenantProvision, scope], HttpContext.RequestAborted);
        return authority.IsError ? null : authority.Result;
    }

    /// <summary>List the tenant's child users (optionally filtered by external user id).</summary>
    [HttpGet("users")]
    public async Task<ActionResult<AZOAResult<IEnumerable<ChildUserResponse>>>> ListChildren([FromQuery] string? externalUserId)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.ListChildrenAsync(tenantId.Value, externalUserId, HttpContext.RequestAborted);
        if (result.IsError) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>Resolve one child by the tenant's own external user id.</summary>
    [HttpGet("users/{externalUserId}")]
    public async Task<ActionResult<AZOAResult<ChildUserResponse>>> ResolveChild(string externalUserId)
    {
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null) return Unauthorized();

        var result = await _manager.ResolveChildAsync(tenantId.Value, externalUserId, HttpContext.RequestAborted);
        return TranslateResult(result);
    }

    /// <summary>Issue a short-lived child credential clamped to tenant ∩ live grant ∩ allowlisted-endpoint scopes.</summary>
    [HttpPost("users/{id:guid}/credential")]
    [EnableRateLimiting("tenant-custodial")]
    public async Task<ActionResult<AZOAResult<ChildCredentialResponse>>> IssueChildCredential(
        Guid id,
        [FromBody] IssueChildCredentialModel? model,
        [FromServices] IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(DelegatedChildCredentialGate.EnabledKey))
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                AZOAResult<ChildCredentialResponse>.Failure("Delegated child credentials are disabled on this node."));
        var tenantId = GetTenantIdFromClaims();
        if (tenantId is null || User.GetActingTenantId() is not null || AzoaClaims.IsChildCredential(User)
            || !User.IsApiKey() || !Guid.TryParse(User.FindFirst("ApiKeyId")?.Value, out var issuingKeyId)) return Unauthorized();

        var ceiling = User.GetScopes().Where(DelegatedChildCredentialCatalog.Scopes.Contains).ToList();
        if (ceiling.Count == 0)
            return MissingScope<ChildCredentialResponse>("a scope accepted by a child-credential endpoint");

        var result = await _manager.IssueChildCredentialAsync(
            tenantId.Value, id, model?.Scopes ?? [], ceiling, issuingKeyId, HttpContext.RequestAborted);
        if (!result.IsError) Response.Headers.CacheControl = "no-store";
        return TranslateResult(result);
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
    /// The tenant id is the authenticated key's owner user id — ALWAYS from the
    /// claim, never from a request body. Mirrors
    /// <c>ActionGraphBundleController.GetUserIdFromClaims</c>.
    /// </summary>
    private Guid? GetTenantIdFromClaims()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
