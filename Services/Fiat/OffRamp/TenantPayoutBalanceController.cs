// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Body of <c>POST /api/fiat/tenant-payout-balance/top-up</c>.</summary>
public sealed class TenantPayoutTopUpRequest
{
    /// <summary>Caller-stable key; the same key resumes the same top-up.</summary>
    public string RequestKey { get; set; } = string.Empty;

    /// <summary>Amount the tenant pays, in minor units of the node's currency.</summary>
    public long AmountMinorUnits { get; set; }

    public string? SuccessUrl { get; set; }
    public string? CancelUrl { get; set; }
}

/// <summary>
/// D-8 tenant payout balance: the tenant reads its own floats and tops them up through Checkout.
/// The tenant is always the authenticated principal itself (API key owner or the tenant's login).
/// </summary>
[ApiController]
[Route("api/fiat/tenant-payout-balance")]
[Authorize]
public sealed class TenantPayoutBalanceController(
    ITenantPayoutFunding funding, IValueRailProviderFactory rails, FiatOptions fiat) : ControllerBase
{
    /// <summary>Every payout float this tenant holds: available, reserved, finalized, pending credits, deficit.</summary>
    [HttpGet]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<TenantPayoutFloatView>>>> Get(CancellationToken ct)
    {
        if (ResolveTenant() is not { } tenant)
            return StatusCode(StatusCodes.Status403Forbidden, AZOAResult<IReadOnlyList<TenantPayoutFloatView>>.FailureWithCode(
                "A tenant principal is required.", AzoaErrorCodes.Forbidden));
        return Ok(AZOAResult<IReadOnlyList<TenantPayoutFloatView>>.Success(await funding.DescribeAsync(tenant, ct), "Success"));
    }

    /// <summary>Starts (or resumes) a Checkout top-up; the float is credited net of fees once the payment matures.</summary>
    [HttpPost("top-up")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<FiatCheckoutIntent>>> TopUp([FromBody] TenantPayoutTopUpRequest request, CancellationToken ct)
    {
        if (ResolveTenant() is not { } tenant)
            return StatusCode(StatusCodes.Status403Forbidden, AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "A tenant principal is required.", AzoaErrorCodes.Forbidden));
        if (request is null || string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Trim().Length > 128)
            return BadRequest(AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "A stable request key (1-128 characters) is required.", AzoaErrorCodes.InvalidRequest));
        if (!rails.TryGetRailProvider(FiatRailIds.UsdRail, fiat.ActiveEnvironment, out var provider)
            || provider is not IFiatOnRampRail rail)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, FiatConsumerErrors.RailUnavailable<FiatCheckoutIntent>());

        var result = await rail.BeginCheckoutAsync(new FiatCheckoutRequest
        {
            SubjectUserId = tenant,
            TenantId = null,
            AdmissionKey = TenantPayoutTopUp.AdmissionKeyFor(tenant, request.RequestKey),
            Purpose = TenantPayoutTopUp.Purpose,
            AmountMinorUnits = request.AmountMinorUnits,
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
        }, ct);
        if (!result.IsError)
            return Ok(result);
        return result.Code switch
        {
            AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
            AzoaErrorCodes.Conflict => Conflict(result),
            AzoaErrorCodes.DependencyUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            _ => BadRequest(result),
        };
    }

    /// <summary>The tenant is the principal itself; an API key additionally needs <c>tenant:provision</c>.</summary>
    private Guid? ResolveTenant()
    {
        var sub = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var caller) || caller == Guid.Empty)
            return null;
        var isApiKey = string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase);
        if (isApiKey && !User.HasScope(AzoaScopes.TenantProvision))
            return null;
        // A delegated principal acting for another tenant is never that tenant's treasurer.
        return User.GetActingTenantId() is { } acting && acting != caller ? null : caller;
    }
}

/// <summary>Operator view of tenant payout floats with the entry-vs-counter consistency check.</summary>
[ApiController]
[Route("api/admin/tenant-payout-floats")]
[Authorize(Policy = "Operator")]
public sealed class TenantPayoutOperatorController(ITenantPayoutFunding funding) : ControllerBase
{
    /// <summary>Every (currency, environment) float of one tenant, recomputed from entries in one read.</summary>
    [HttpGet("{tenantId:guid}")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<TenantPayoutFloatView>>>> Get(Guid tenantId, CancellationToken ct)
        => Ok(AZOAResult<IReadOnlyList<TenantPayoutFloatView>>.Success(await funding.DescribeAsync(tenantId, ct), "Success"));
}
