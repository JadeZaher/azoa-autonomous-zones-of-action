// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Kyb;

namespace AZOA.WebAPI.Controllers;

/// <summary>What a registrant posts to declare (or retire) the price money buys units at.</summary>
public sealed class PriceScheduleRegisterRequest
{
    /// <summary>Chain the priced asset lives on, as the provider spells it.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The asset the money buys.</summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>ISO-4217 code of the money. USD only today (D15).</summary>
    public string CurrencyCode { get; set; } = PriceScheduleLimits.SupportedCurrencyCode;

    /// <summary>
    /// Base units of the asset that ONE minor unit of the currency buys, as an exact decimal
    /// string. At two decimals, "1 unit = $0.01" is <c>"100"</c>.
    /// </summary>
    public string UnitsPerMinorUnit { get; set; } = string.Empty;

    /// <summary>The registrant's own name for where this price came from.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Free-text note. Never interpreted.</summary>
    public string? Note { get; set; }

    /// <summary>When the registrant OBSERVED this price. Defaults to now.</summary>
    public DateTimeOffset? DeclaredAt { get; set; }

    /// <summary>The version the caller believes is current; a stale value is refused.</summary>
    public long? ExpectedCurrentVersion { get; set; }

    /// <summary>Register this version as retired: stop selling units at any price.</summary>
    public bool Retire { get; set; }
}

/// <summary>One registered price version, as a caller sees it.</summary>
public sealed class PriceScheduleResponse
{
    public string ChainType { get; set; } = string.Empty;

    public string AssetId { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = string.Empty;

    public long Version { get; set; }

    /// <summary>Base units per one minor unit of currency, exactly as registered.</summary>
    public string UnitsPerMinorUnit { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary><c>Active</c> or <c>Retired</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTimeOffset DeclaredAt { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>
    /// Projects one row. The scope key and the record id are NOT projected: a caller only ever
    /// sees its own scope, so echoing the scope would say nothing and echoing the id would
    /// publish an internal key for no gain.
    /// </summary>
    public static PriceScheduleResponse From(RegisteredPriceSchedule schedule) => new()
    {
        ChainType = schedule.ChainType,
        AssetId = schedule.AssetId,
        CurrencyCode = schedule.CurrencyCode,
        Version = schedule.Version,
        UnitsPerMinorUnit = PriceScheduleMath.FormatRate(schedule.UnitsPerMinorUnit),
        Source = schedule.Source,
        Note = schedule.Note,
        Status = schedule.Status,
        DeclaredAt = schedule.DeclaredAt,
        RegisteredAt = schedule.RegisteredAt,
    };
}

/// <summary>
/// Registration and read of the price at which money buys units on this node's fiat on-ramp.
///
/// <para><b>Scope.</b> A registration always lands in the CALLER's own scope, resolved from the
/// principal and never from the body. A tenant API key registers the tenant's price; a node
/// operator registers the node-wide default. There is deliberately no way to write another
/// party's price: the price is what converts a stranger's money into units, so a cross-scope
/// write would be a money-printer with a REST route in front of it.</para>
///
/// <para><b>Versions.</b> Rows are append-only and the node assigns the version. A caller that
/// quotes <c>ExpectedCurrentVersion</c> is refused when the live sequence has moved on, so two
/// operators cannot both "correct" a price and have the second one silently win.</para>
///
/// <para>See <c>Services/Fiat/OnRamp/AGENTS.md</c> §pricing.</para>
/// </summary>
[ApiController]
[Route("api/fiat/price-schedule")]
[Authorize]
public sealed class TenantPriceScheduleController : ControllerBase
{
    /// <summary>Most versions one history read returns.</summary>
    private const int MaxHistoryPageSize = 100;

    private readonly ITenantPriceScheduleStore _schedules;
    private readonly IKybGate _kyb;

    public TenantPriceScheduleController(ITenantPriceScheduleStore schedules, IKybGate kyb)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _kyb = kyb ?? throw new ArgumentNullException(nameof(kyb));
    }

    /// <summary>Appends the next price version for the caller's own scope.</summary>
    [HttpPost]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<PriceScheduleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AZOAResult<PriceScheduleResponse>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AZOAResult<PriceScheduleResponse>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AZOAResult<PriceScheduleResponse>>> Register(
        [FromBody] PriceScheduleRegisterRequest request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(AZOAResult<PriceScheduleResponse>.FailureWithCode(
                "A price registration is required.", AzoaErrorCodes.InvalidRequest));

        if (!TryResolveScope(out var scopeKey, out var tenantId, out var callerUserId, out var refusal))
            return refusal!;

        // D-6: publishing a tenant payout price needs a current KYB approval. Retiring a price only
        // stops trading, so a tenant whose KYB lapsed can still withdraw its schedule.
        if (tenantId is { } kybTenant && !request.Retire)
        {
            var kyb = await _kyb.RequireVerifiedTenantAsync(kybTenant, KybCapability.PayoutPriceSchedule, ct);
            if (kyb.IsError)
                return StatusCode(StatusCodes.Status403Forbidden,
                    AZOAResult<PriceScheduleResponse>.FailureWithCode(kyb.Message, kyb.Code ?? KybGate.RequiredCode));
        }

        var result = await _schedules.RegisterAsync(
            new PriceScheduleRegistration
            {
                ChainType = request.ChainType,
                AssetId = request.AssetId,
                CurrencyCode = request.CurrencyCode,
                UnitsPerMinorUnit = request.UnitsPerMinorUnit,
                Source = request.Source,
                Note = request.Note,
                DeclaredAt = request.DeclaredAt,
                ExpectedCurrentVersion = request.ExpectedCurrentVersion,
                Retire = request.Retire,
            },
            scopeKey,
            tenantId,
            callerUserId,
            ct);

        if (result.IsError || result.Result is null)
        {
            var failure = new AZOAResult<PriceScheduleResponse>
            {
                IsError = true,
                Message = result.Message,
                Code = result.Code,
            };

            return result.Code switch
            {
                AzoaErrorCodes.Conflict => Conflict(failure),
                AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, failure),
                _ => BadRequest(failure),
            };
        }

        return Ok(AZOAResult<PriceScheduleResponse>.Success(
            PriceScheduleResponse.From(result.Result), result.Message));
    }

    /// <summary>The current price version for the caller's own scope, whatever its status.</summary>
    [HttpGet("{chainType}/{assetId}")]
    [ProducesResponseType(typeof(AZOAResult<PriceScheduleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AZOAResult<PriceScheduleResponse>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AZOAResult<PriceScheduleResponse>>> GetCurrent(
        string chainType, string assetId, CancellationToken ct)
    {
        if (!TryResolveScope(out var scopeKey, out _, out _, out var refusal))
            return refusal!;

        var result = await _schedules.GetLatestAsync(scopeKey, chainType, assetId, ct);
        if (result.IsError)
            return BadRequest(new AZOAResult<PriceScheduleResponse>
            {
                IsError = true,
                Message = result.Message,
                Code = result.Code,
            });

        if (result.Result is null)
            return NotFound(AZOAResult<PriceScheduleResponse>.FailureWithCode(
                "No price is registered for that asset.", AzoaErrorCodes.NotFound));

        return Ok(AZOAResult<PriceScheduleResponse>.Success(
            PriceScheduleResponse.From(result.Result), "Success"));
    }

    /// <summary>Price history for the caller's own scope, newest first.</summary>
    [HttpGet("{chainType}/{assetId}/versions")]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<PriceScheduleResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<PriceScheduleResponse>>>> GetVersions(
        string chainType, string assetId, [FromQuery] int limit, CancellationToken ct)
    {
        if (!TryResolveScope(out var scopeKey, out _, out _, out _))
        {
            // The refusal is typed for the single-value routes; restate it for this one rather
            // than casting, so the body a caller receives is always the shape it expects.
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<IReadOnlyList<PriceScheduleResponse>>.FailureWithCode(
                    "This principal may not read a price schedule.", AzoaErrorCodes.Forbidden));
        }

        var page = limit <= 0 ? MaxHistoryPageSize : Math.Min(limit, MaxHistoryPageSize);
        var result = await _schedules.ListVersionsAsync(scopeKey, chainType, assetId, page, ct);
        if (result.IsError)
            return BadRequest(new AZOAResult<IReadOnlyList<PriceScheduleResponse>>
            {
                IsError = true,
                Message = result.Message,
                Code = result.Code,
            });

        return Ok(AZOAResult<IReadOnlyList<PriceScheduleResponse>>.Success(
            (result.Result ?? Array.Empty<RegisteredPriceSchedule>())
                .Select(PriceScheduleResponse.From)
                .ToList(),
            "Success"));
    }

    // ─── authorisation ───

    /// <summary>
    /// Resolves the scope this principal writes and reads, or refuses. The scope comes from the
    /// PRINCIPAL — there is no body or route field that can name a different one.
    /// </summary>
    private bool TryResolveScope(
        out string scopeKey,
        out Guid? tenantId,
        out Guid callerUserId,
        out ActionResult<AZOAResult<PriceScheduleResponse>>? refusal)
    {
        scopeKey = PriceScheduleScope.Node;
        tenantId = null;
        callerUserId = Guid.Empty;
        refusal = null;

        var caller = GetUserIdFromClaims();
        if (caller is null || caller.Value == Guid.Empty)
        {
            refusal = Unauthorized(AZOAResult<PriceScheduleResponse>.FailureWithCode(
                "Invalid token.", AzoaErrorCodes.Forbidden));
            return false;
        }

        callerUserId = caller.Value;

        var isApiKey = string.Equals(
            User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase);

        if (isApiKey)
        {
            if (!User.HasScope(AzoaScopes.TenantProvision))
            {
                refusal = StatusCode(StatusCodes.Status403Forbidden,
                    AZOAResult<PriceScheduleResponse>.FailureWithCode(
                        $"Caller lacks the '{AzoaScopes.TenantProvision}' scope required to register a price.",
                        AzoaErrorCodes.Forbidden));
                return false;
            }

            // An API key carries no act_as_tenant claim, so the tenant IS the key's owner — the
            // same resolution FiatCheckoutController performs. A principal that somehow named a
            // DIFFERENT tenant is a refusal, never a coin flip.
            var claimedTenantId = User.GetActingTenantId();
            if (claimedTenantId is { } fromClaim && fromClaim != callerUserId)
            {
                refusal = StatusCode(StatusCodes.Status403Forbidden,
                    AZOAResult<PriceScheduleResponse>.FailureWithCode(
                        "The principal names two different acting tenants.", AzoaErrorCodes.Forbidden));
                return false;
            }

            tenantId = callerUserId;
            scopeKey = PriceScheduleScope.For(callerUserId);
            return true;
        }

        // Not an API key: the node-wide default is the operator's to set, and operator authority
        // originates only from a JWT (the Operator policy's own first axis).
        var isOperator = AzoaClaims.IsNodeOperator(User)
            && User.HasScope(AzoaScopes.Operator)
            && User.GetActingTenantId() is null;

        if (!isOperator)
        {
            refusal = StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<PriceScheduleResponse>.FailureWithCode(
                    "Only a node operator may set the node-wide price, and only a tenant credential may "
                    + "set a tenant's own.",
                    AzoaErrorCodes.Forbidden));
            return false;
        }

        scopeKey = PriceScheduleScope.Node;
        tenantId = null;
        return true;
    }

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
