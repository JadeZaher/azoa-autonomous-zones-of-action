using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/operator")]
[Authorize(Policy = "NodeOperatorSession")]
public sealed class NodeOperatorController : ControllerBase
{
    private readonly IKycControlPlaneManager _manager;

    public NodeOperatorController(IKycControlPlaneManager manager)
    {
        _manager = manager;
    }

    [HttpGet("overview")]
    public async Task<ActionResult<AZOAResult<NodeOperatorOverviewResponse>>> GetOverview(CancellationToken ct)
        => Translate(await _manager.GetOverviewAsync(ct));

    [HttpGet("kyc/providers")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<KycProviderProfileResponse>>>> ListProviders(CancellationToken ct)
        => Translate(await _manager.ListProfilesAsync(ct));

    [HttpGet("kyc/providers/{providerKey}")]
    public async Task<ActionResult<AZOAResult<KycProviderProfileResponse>>> GetProvider(
        string providerKey,
        CancellationToken ct)
    {
        var result = await _manager.ListProfilesAsync(ct);
        if (result.IsError)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                AZOAResult<KycProviderProfileResponse>.FailureWithCode(
                    result.Message,
                    result.Code ?? AzoaErrorCodes.DependencyUnavailable));
        var profile = result.Result!.SingleOrDefault(candidate =>
            string.Equals(candidate.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
        return profile is null
            ? NotFound(AZOAResult<KycProviderProfileResponse>.FailureWithCode(
                "KYC provider profile not found.",
                AzoaErrorCodes.NotFound))
            : Ok(AZOAResult<KycProviderProfileResponse>.Success(profile));
    }

    [HttpPut("kyc/providers/{providerKey}")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [EnableRateLimiting("financial")]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<AZOAResult<KycProviderProfileResponse>>> UpdateProvider(
        string providerKey,
        [FromBody] UpdateKycProviderProfileRequest request,
        CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var operatorId))
            return Unauthorized();
        return Translate(await _manager.UpdateProfileAsync(providerKey, request, operatorId, ct));
    }

    [HttpGet("tenants")]
    public async Task<ActionResult<AZOAResult<CursorPage<OperatorTenantKycSummaryResponse>>>> ListTenants(
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
        => Translate(await _manager.ListTenantsAsync(limit, cursor, search, ct));

    [HttpPut("tenants/{tenantId:guid}/kyc-provider")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [EnableRateLimiting("financial")]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<AZOAResult<TenantKycSelectionResponse>>> SelectTenantProvider(
        Guid tenantId,
        [FromBody] SelectTenantKycProviderRequest request,
        CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var operatorId))
            return Unauthorized();
        return Translate(await _manager.SelectTenantProviderAsync(
            tenantId, request, operatorId, false, ct));
    }

    [HttpGet("kyc/submissions")]
    public async Task<ActionResult<AZOAResult<CursorPage<OperatorKycSubmissionQueueItem>>>> ListSubmissions(
        [FromQuery] string status = "pending",
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
        => Translate(await _manager.ListQueueAsync(status, limit, cursor, ct));

    [HttpGet("kyc/audit")]
    public async Task<ActionResult<AZOAResult<CursorPage<KycControlAuditResponse>>>> ListAudit(
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null,
        [FromQuery] Guid? tenantId = null,
        [FromQuery] string? providerKey = null,
        [FromQuery] string? action = null,
        CancellationToken ct = default)
        => Translate(await _manager.ListAuditAsync(
            limit, cursor, tenantId, providerKey, action, ct));

    [HttpPost("kyc/submissions/{id:guid}/decision")]
    [Authorize(Policy = "RecentNodeOperatorSession")]
    [EnableRateLimiting("financial")]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<AZOAResult<OperatorKycSubmissionQueueItem>>> Decide(
        Guid id,
        [FromBody] OperatorKycDecisionRequest request,
        CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(User, out var operatorId))
            return Unauthorized();
        return Translate(await _manager.DecideAsync(id, request, operatorId, ct));
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
