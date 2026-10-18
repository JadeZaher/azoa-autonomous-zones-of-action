using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/tenant/kyc")]
[Authorize(Policy = "FirstPartyLogin")]
public sealed class TenantKycConfigurationController : ControllerBase
{
    private readonly IKycControlPlaneManager _manager;

    public TenantKycConfigurationController(IKycControlPlaneManager manager)
    {
        _manager = manager;
    }

    [HttpGet("providers")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>>> ListProviders(CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var tenantId))
            return Unauthorized();
        return Translate(await _manager.ListTenantChoicesAsync(tenantId, ct));
    }

    [HttpGet("provider")]
    public async Task<ActionResult<AZOAResult<TenantKycSelectionResponse>>> GetProvider(CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var tenantId))
            return Unauthorized();
        return Translate(await _manager.GetTenantSelectionAsync(tenantId, true, ct));
    }

    /// <summary>
    /// Repoints this tenant's KYC provider. OFF by default: the manager refuses with
    /// <c>TENANT_PROVIDER_OVERRIDE_NOT_ENABLED</c> unless the operator has switched
    /// <c>Kyc:TenantProviderOverride:Enabled</c> on AND listed this tenant in
    /// <c>Kyc:TenantProviderOverride:AllowedTenantIds</c>. The provider IS the
    /// compliance policy, so choosing one is the node operator's call by default.
    /// </summary>
    [HttpPut("provider")]
    [Authorize(Policy = "RecentFirstPartyLogin")]
    [EnableRateLimiting("financial")]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<AZOAResult<TenantKycSelectionResponse>>> SelectProvider(
        [FromBody] SelectTenantKycProviderRequest request,
        CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var tenantId))
            return Unauthorized();
        return Translate(await _manager.SelectTenantProviderAsync(
            tenantId, request, tenantId, true, ct: ct));
    }

    private ActionResult<AZOAResult<T>> Translate<T>(AZOAResult<T> result)
    {
        if (!result.IsError)
            return Ok(result);
        return result.Code switch
        {
            AzoaErrorCodes.NotFound => NotFound(result),
            AzoaErrorCodes.Conflict => Conflict(result),
            AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
            AzoaErrorCodes.InvalidRequest
                or AzoaErrorCodes.PolicyUnavailable
                or AzoaErrorCodes.OperationNotAllowed => BadRequest(result),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        };
    }
}
