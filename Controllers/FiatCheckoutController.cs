// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Controllers;

/// <summary>What a caller posts to start a payment. The amount is MONEY, never a unit count.</summary>
public sealed class FiatCheckoutStartRequest
{
    /// <summary>
    /// Caller-stable key. The same key returns the same payment instead of opening a second one,
    /// so a double-tapped button cannot charge twice.
    /// </summary>
    public string RequestKey { get; set; } = string.Empty;

    /// <summary>Allowlisted routing value; see <c>Fiat:Checkout:AllowedPurposes</c>.</summary>
    public string Purpose { get; set; } = FiatCheckoutPurposes.FundsTopUp;

    /// <summary>The amount to pay, in minor units of the node's configured currency.</summary>
    public long AmountMinorUnits { get; set; }

    /// <summary>Where to return the payer afterwards. Must be on an allowlisted origin.</summary>
    public string? SuccessUrl { get; set; }

    /// <summary>Where to return the payer if they back out. Must be on an allowlisted origin.</summary>
    public string? CancelUrl { get; set; }
}

/// <summary>
/// The "add funds" surface: start a payment for a person's account, and find that payment again
/// when they come back.
///
/// <para><b>Authorisation.</b> The subject is always the <c>userId</c> ROUTE value; no body field
/// can redirect a payment to a different account. A first-party login may only pay for itself. An
/// API-key principal is a tenant acting for somebody else, so it additionally needs that person's
/// live consent — the same gate <c>AllocationController</c> applies, probed with the same signing
/// scope the issuance this payment funds will later use.</para>
///
/// <para><b>What this controller does NOT do.</b> It never credits anything. A payment becomes
/// funds only when the provider's own webhook clears through
/// <c>StripeWebhookController</c> and a registered settlement handler applies it, which is why the
/// intent read reports <c>payment_received</c> until that has actually happened.</para>
/// </summary>
[ApiController]
[Route("api/fiat/checkout")]
[Authorize]
public sealed class FiatCheckoutController : ControllerBase
{
    private readonly IValueRailProviderFactory _railFactory;
    private readonly FiatOptions _options;
    private readonly ITenantConsentGate? _consentGate;

    /// <param name="consentGate">
    /// The live consent check (registered in <c>Program.cs</c>). Optional only so the type still
    /// constructs without a container; a tenant-driven request that finds no gate is REFUSED.
    /// </param>
    public FiatCheckoutController(
        IValueRailProviderFactory railFactory,
        FiatOptions options,
        ITenantConsentGate? consentGate = null)
    {
        _railFactory = railFactory;
        _options = options;
        _consentGate = consentGate;
    }

    /// <summary>Whether this node can take a payment, and which config keys are missing if not.</summary>
    [HttpGet("capabilities")]
    public ActionResult<AZOAResult<HostedSessionCapabilities>> Capabilities()
    {
        if (!TryResolveRail(out var rail, out var unavailable))
            return Ok(AZOAResult<HostedSessionCapabilities>.Success(unavailable!, "Success"));

        return Ok(AZOAResult<HostedSessionCapabilities>.Success(rail!.GetCapabilities(), "Success"));
    }

    /// <summary>Starts (or resumes) a payment that adds funds to <paramref name="userId"/>'s account.</summary>
    [HttpPost("{userId:guid}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<FiatCheckoutIntent>>> Start(
        Guid userId, [FromBody] FiatCheckoutStartRequest request)
    {
        if (request is null)
            return BadRequest(AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "A payment request is required.", AzoaErrorCodes.InvalidRequest));

        if (string.IsNullOrWhiteSpace(request.RequestKey))
            return BadRequest(AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "A stable request key is required so a repeated request cannot charge twice.",
                AzoaErrorCodes.InvalidRequest));

        if (!TryResolveRail(out var rail, out _))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                FiatConsumerErrors.Unresolved<FiatCheckoutIntent>(_options));

        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "Invalid token.", AzoaErrorCodes.Forbidden));

        var (actingTenantId, authorisation) = await AuthoriseSubjectAsync<FiatCheckoutIntent>(userId, callerUserId.Value);
        if (authorisation is not null)
            return authorisation;

        var result = await rail!.BeginCheckoutAsync(
            new FiatCheckoutRequest
            {
                SubjectUserId = userId,
                TenantId = actingTenantId,
                AdmissionKey = BuildAdmissionKey(userId, actingTenantId, request.RequestKey),
                Purpose = request.Purpose,
                AmountMinorUnits = request.AmountMinorUnits,
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
            },
            HttpContext.RequestAborted);

        if (!result.IsError)
            return Ok(result);

        return result.Code switch
        {
            AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
            AzoaErrorCodes.Conflict => Conflict(result),
            // Same-key replay codes (F-?: replay-state machine codes): distinct situations, same
            // 409 the caller always got; the new `code` field is what lets it tell them apart.
            FiatConsumerErrors.RequestKeyInUseCode => Conflict(result),
            FiatConsumerErrors.IntentFinishedCode => Conflict(result),
            FiatConsumerErrors.IntentExpiredCode => Conflict(result),
            FiatConsumerErrors.IntentNeedsReviewCode => Conflict(result),
            FiatConsumerErrors.RequestVoidedCode => Conflict(result),
            AzoaErrorCodes.DependencyUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            FiatConsumerErrors.RailUnavailableCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            FiatConsumerErrors.PaymentsNotConfiguredCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            FiatConsumerErrors.RailBusyCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            _ => BadRequest(result),
        };
    }

    /// <summary>
    /// Voids <paramref name="requestKey"/> so nothing can ever be paid under it, or reveals the payment
    /// already made under it. A 200 <c>voided</c> lets the caller discard the key; anything else means
    /// keep it. See <c>Providers/Fiat/AGENTS.md</c> §request-void.
    /// </summary>
    [HttpPost("{userId:guid}/requests/{requestKey}/void")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<FiatCheckoutVoidResult>>> VoidRequest(Guid userId, string requestKey)
    {
        if (string.IsNullOrWhiteSpace(requestKey))
            return BadRequest(AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
                "A request key is required.", AzoaErrorCodes.InvalidRequest));

        // Routing leaves an encoded '/' as the literal "%2F", which would name a DIFFERENT key than the
        // one begin stored: a void of it would tombstone a stranger key and answer "voided" falsely.
        if (requestKey.Contains("%2F", StringComparison.OrdinalIgnoreCase))
            return BadRequest(AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
                "A request key containing '/' cannot be voided through this route.", AzoaErrorCodes.InvalidRequest));

        if (!TryResolveRail(out var rail, out _))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                FiatConsumerErrors.Unresolved<FiatCheckoutVoidResult>(_options));

        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
                "Invalid token.", AzoaErrorCodes.Forbidden));

        var (actingTenantId, authorisation) = await AuthoriseSubjectAsync<FiatCheckoutVoidResult>(userId, callerUserId.Value);
        if (authorisation is not null)
            return authorisation;

        var result = await rail!.VoidRequestAsync(
            userId, actingTenantId, BuildAdmissionKey(userId, actingTenantId, requestKey), HttpContext.RequestAborted);
        if (!result.IsError)
            return Ok(result);

        return result.Code switch
        {
            AzoaErrorCodes.Conflict => Conflict(result),
            FiatConsumerErrors.RequestKeyInUseCode => Conflict(result),
            FiatConsumerErrors.IntentNeedsReviewCode => Conflict(result),
            AzoaErrorCodes.DependencyUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            FiatConsumerErrors.RailUnavailableCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            FiatConsumerErrors.PaymentsNotConfiguredCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            FiatConsumerErrors.RailBusyCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            _ => BadRequest(result),
        };
    }

    /// <summary>
    /// Reads one payment for the person it belongs to. This is what the return page calls with the
    /// <c>intent</c> query parameter the rail put on the return URL.
    /// </summary>
    [HttpGet("{userId:guid}/{intentId:guid}")]
    public async Task<ActionResult<AZOAResult<FiatCheckoutIntent>>> Get(Guid userId, Guid intentId)
    {
        if (!TryResolveRail(out var rail, out _))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                FiatConsumerErrors.Unresolved<FiatCheckoutIntent>(_options));

        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "Invalid token.", AzoaErrorCodes.Forbidden));

        var (actingTenantId, authorisation) = await AuthoriseSubjectAsync<FiatCheckoutIntent>(userId, callerUserId.Value);
        if (authorisation is not null)
            return authorisation;

        var result = await rail!.GetIntentAsync(userId, intentId, actingTenantId, HttpContext.RequestAborted);
        if (!result.IsError)
            return Ok(result);

        // A read whose settlement state could not be confirmed (the provider re-read failed) FAILS
        // outright rather than answering 200 with an invented `not_started`; see
        // FiatRailProvider.ProjectAsync and Providers/Fiat/AGENTS.md §settlement-read-failure. The
        // client is expected to treat this as transient and retry, never as "no payment yet".
        if (result.Code == AzoaErrorCodes.DependencyUnavailable)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, result);

        return result.Code == AzoaErrorCodes.NotFound ? NotFound(result) : BadRequest(result);
    }

    // ─── authorisation ───

    /// <summary>
    /// Decides whether this principal may act for <paramref name="subjectUserId"/>, and resolves the
    /// acting tenant when one is involved. Returns <c>(tenantId, null)</c> to proceed, or
    /// <c>(null, refusal)</c> to stop; the refusal body is an <c>AZOAResult&lt;T&gt;</c> of the calling action.
    /// </summary>
    private async Task<(Guid? TenantId, ActionResult? Refusal)>
        AuthoriseSubjectAsync<T>(Guid subjectUserId, Guid callerUserId)
    {
        var isApiKey = string.Equals(
            User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase);

        if (!isApiKey)
        {
            // A first-party login pays for ITSELF. Letting a login name another account would make
            // this endpoint a way to attach somebody else's card history to a stranger's account.
            if (subjectUserId != callerUserId)
            {
                return (null, StatusCode(StatusCodes.Status403Forbidden,
                    AZOAResult<T>.FailureWithCode(
                        "You can only add funds to your own account.", AzoaErrorCodes.Forbidden)));
            }

            return (null, null);
        }

        if (!User.HasScope(AzoaScopes.TenantProvision))
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<T>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.TenantProvision}' scope required to act for an account.",
                    AzoaErrorCodes.Forbidden)));
        }

        // An API key carries no act_as_tenant claim, so the tenant IS the key's owner — the same
        // resolution AllocationController performs. A principal that somehow named a DIFFERENT
        // tenant is a refusal, never a coin flip.
        var claimedTenantId = User.GetActingTenantId();
        if (claimedTenantId is { } fromClaim && fromClaim != callerUserId)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<T>.FailureWithCode(
                    "The principal names two different acting tenants.", AzoaErrorCodes.Forbidden)));
        }

        if (callerUserId == Guid.Empty)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<T>.FailureWithCode(
                    "The principal carries no resolvable tenant identity.", AzoaErrorCodes.Forbidden)));
        }

        if (_consentGate is null)
        {
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable,
                AZOAResult<T>.FailureWithCode(
                    "Consent cannot be verified; the payment was refused.",
                    AzoaErrorCodes.DependencyUnavailable)));
        }

        // Probe the scope the funded issuance signs under (IssueUnits => units:issue, OI-23); see
        // Controllers/AGENTS.md §fiat-checkout-consent.
        var probe = new SigningContext(subjectUserId, Guid.Empty, IsPlatform: false)
            .ActingAs(callerUserId, AzoaScopes.UnitsIssue, subjectUserId);

        var consent = await _consentGate.EnsureAllowedAsync(probe, HttpContext.RequestAborted);
        if (consent.IsError)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<T>.FailureWithCode(consent.Message, AzoaErrorCodes.Forbidden)));
        }

        return (callerUserId, null);
    }

    // ─── helpers ───

    /// <summary>
    /// Resolves the bound fiat rail, or reports why it is unavailable without leaking configuration.
    /// </summary>
    private bool TryResolveRail(out IFiatOnRampRail? rail, out HostedSessionCapabilities? unavailable)
    {
        rail = null;
        unavailable = null;

        if (_railFactory.TryGetRailProvider(
                FiatRailIds.UsdRail, _options.ActiveEnvironment, out IValueRailProvider? provider)
            && provider is IFiatOnRampRail onRamp)
        {
            rail = onRamp;
            return true;
        }

        unavailable = new HostedSessionCapabilities
        {
            ProviderKey = FiatRailIds.StripeProviderKey,
            FlowKind = HostedSessionFlowKinds.FiatCheckout,
            Available = false,
            MissingConfigurationKeys = _options.MissingKeysFor(_options.ActiveEnvironment),
            UnavailableReason = "Adding funds is not configured on this node.",
        };
        return false;
    }

    /// <summary>
    /// Namespaces the caller's request key by SUBJECT and DRIVER (tenant id, or <c>self</c> for a
    /// first-party login; F-H8). The admission row is unique on
    /// (flow, provider, admission key) across the whole node, so an un-namespaced key would let one
    /// caller's ordinary value — "1", "top-up" — collide with another account's live payment.
    /// A blank key is REFUSED rather than replaced with a fresh one: a generated key makes every
    /// retry a new payment, which is the double-charge the key exists to prevent.
    /// </summary>
    private static string BuildAdmissionKey(Guid subjectUserId, Guid? tenantId, string requestKey)
        => $"{subjectUserId:N}:{(tenantId is { } tenant ? tenant.ToString("N") : "self")}:{requestKey.Trim()}";

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
