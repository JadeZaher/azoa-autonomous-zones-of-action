// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>What a caller posts to cash out. The amount is MONEY; the node works out the rest.</summary>
public sealed class CashOutStartRequest
{
    /// <summary>
    /// Caller-stable key. The same key returns the same cash-out instead of starting a second
    /// one, so a double-tapped button cannot pay twice.
    /// </summary>
    public string RequestKey { get; set; } = string.Empty;

    /// <summary>The amount to send, in minor units of the node's configured currency.</summary>
    public long AmountMinorUnits { get; set; }
}

/// <summary>
/// The "cash out" surface: set up where a person's money should go, see what they can take out,
/// and take it out.
///
/// <para><b>Authorisation.</b> The subject is always the <c>userId</c> ROUTE value; no body field
/// can redirect a cash-out to a different account, and the destination bank account is resolved
/// by the node from that person's own durable onboarding record rather than from anything a
/// caller sends. A first-party login may only cash out for itself. An API-key principal is a
/// tenant acting for somebody else, so it additionally needs that person's live consent — the
/// same gate <c>FiatCheckoutController</c> applies, probed with the same signing scope the move
/// this cash-out performs will later use.</para>
///
/// <para><b>What this controller does NOT do.</b> It never moves value inline. A request claims a
/// durable cash-out and hands it to the saga; the units leave and the money arrives on that
/// saga's clock, which is why the read surface reports a status rather than a receipt.</para>
/// </summary>
[ApiController]
[Route("api/fiat/payouts")]
[Authorize]
public sealed class FiatPayoutController : ControllerBase
{
    private const int DefaultHistoryLimit = 25;

    private readonly IOffRamp _offRamp;
    private readonly IPayoutAccountService _accounts;
    private readonly ITenantConsentGate? _consentGate;

    /// <param name="consentGate">
    /// The live consent check (registered in <c>Program.cs</c>). Optional only so the type still
    /// constructs without a container; a tenant-driven request that finds no gate is REFUSED.
    /// </param>
    public FiatPayoutController(
        IOffRamp offRamp,
        IPayoutAccountService accounts,
        ITenantConsentGate? consentGate = null)
    {
        _offRamp = offRamp;
        _accounts = accounts;
        _consentGate = consentGate;
    }

    /// <summary>What this person can cash out today, and why the rest is still held.</summary>
    [HttpGet("{userId:guid}/availability")]
    public async Task<ActionResult<AZOAResult<CashOutQuote>>> Availability(Guid userId)
    {
        var authorised = await AuthoriseAsync<CashOutQuote>(userId);
        if (authorised.Refusal is not null)
            return authorised.Refusal;

        var result = await _offRamp.GetQuoteAsync(
            userId, authorised.TenantId, HttpContext.RequestAborted);
        if (result.IsError)
            return Map(result);

        return Ok(result);
    }

    /// <summary>Where this person's money goes, and whether it is ready to receive any.</summary>
    [HttpGet("{userId:guid}/account")]
    public async Task<ActionResult<AZOAResult<PayoutAccountStatus>>> Account(Guid userId)
    {
        var authorised = await AuthoriseAsync<PayoutAccountStatus>(userId);
        if (authorised.Refusal is not null)
            return authorised.Refusal;

        var result = await _accounts.GetStatusAsync(userId, HttpContext.RequestAborted);
        if (result.IsError)
            return Map(result);

        return Ok(result);
    }

    /// <summary>
    /// Starts (or resumes) payout-account setup and hands back a fresh hosted link. This is the
    /// one moment paperwork is expected, so it happens at the first cash-out rather than at
    /// sign-up.
    /// </summary>
    [HttpPost("{userId:guid}/account")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<PayoutAccountStatus>>> BeginAccountSetup(Guid userId)
    {
        var authorised = await AuthoriseAsync<PayoutAccountStatus>(userId);
        if (authorised.Refusal is not null)
            return authorised.Refusal;

        var result = await _accounts.BeginSetupAsync(
            userId, authorised.TenantId, HttpContext.RequestAborted);

        if (result.IsError)
            return Map(result);

        return Ok(result);
    }

    /// <summary>Claims a cash-out for this person and hands it to the saga.</summary>
    [HttpPost("{userId:guid}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<CashOutView>>> Start(
        Guid userId, [FromBody] CashOutStartRequest request)
    {
        if (request is null)
            return BadRequest(AZOAResult<CashOutView>.FailureWithCode(
                "A cash-out request is required.", AzoaErrorCodes.InvalidRequest));

        if (string.IsNullOrWhiteSpace(request.RequestKey))
            return BadRequest(AZOAResult<CashOutView>.FailureWithCode(
                "A stable request key is required so a repeated request cannot pay twice.",
                AzoaErrorCodes.InvalidRequest));

        var authorised = await AuthoriseAsync<CashOutView>(userId);
        if (authorised.Refusal is not null)
            return authorised.Refusal;

        var result = await _offRamp.RequestAsync(
            new CashOutRequest(
                userId,
                authorised.TenantId,
                // The key is namespaced by SUBJECT inside the ledger's record id, so one caller's
                // ordinary value — "1", "cash-out" — cannot collide with another account's.
                request.RequestKey.Trim(),
                request.AmountMinorUnits),
            HttpContext.RequestAborted);

        if (result.IsError)
            return Map(result);

        return Ok(result);
    }

    /// <summary>This person's own cash-out history, newest first.</summary>
    [HttpGet("{userId:guid}")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<CashOutView>>>> History(
        Guid userId, [FromQuery] int? limit)
    {
        var authorised = await AuthoriseAsync<IReadOnlyList<CashOutView>>(userId);
        if (authorised.Refusal is not null)
            return authorised.Refusal;

        var result = await _offRamp.ListForUserAsync(
            userId, limit ?? DefaultHistoryLimit, HttpContext.RequestAborted);

        if (result.IsError)
            return Map(result);

        return Ok(result);
    }

    // ─── authorisation ───

    /// <summary>
    /// Decides whether this principal may act for <paramref name="subjectUserId"/>, and resolves
    /// the acting tenant when one is involved. A non-null refusal stops the request.
    /// </summary>
    private async Task<(Guid? TenantId, ActionResult<AZOAResult<T>>? Refusal)> AuthoriseAsync<T>(
        Guid subjectUserId)
    {
        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
        {
            return (null, Unauthorized(AZOAResult<T>.FailureWithCode(
                "Invalid token.", AzoaErrorCodes.Forbidden)));
        }

        var isApiKey = string.Equals(
            User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase);

        if (!isApiKey)
        {
            // A first-party login cashes out for ITSELF. Letting a login name another account
            // would make this endpoint a way to move somebody else's balance into your own bank.
            if (subjectUserId != callerUserId)
            {
                return (null, StatusCode(StatusCodes.Status403Forbidden,
                    AZOAResult<T>.FailureWithCode(
                        "You can only cash out from your own account.", AzoaErrorCodes.Forbidden)));
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
        // resolution AllocationController and FiatCheckoutController perform. A principal that
        // somehow named a DIFFERENT tenant is a refusal, never a coin flip.
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
                    "Consent cannot be verified; the cash-out was refused.",
                    AzoaErrorCodes.DependencyUnavailable)));
        }

        // The scope probed is the one the units move under when the saga collects them
        // (transfer:sign), so consent granted here is consent for what actually happens next.
        var probe = new SigningContext(subjectUserId, Guid.Empty, IsPlatform: false)
            .ActingAs(callerUserId.Value, AzoaScopes.TransferSign, subjectUserId);

        var consent = await _consentGate.EnsureAllowedAsync(probe, HttpContext.RequestAborted);
        if (consent.IsError)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<T>.FailureWithCode(consent.Message, AzoaErrorCodes.Forbidden)));
        }

        return (callerUserId, null);
    }

    // ─── helpers ───

    /// <summary>Maps a refusal onto the status code that describes it, without leaking rail text.</summary>
    private ActionResult<AZOAResult<T>> Map<T>(AZOAResult<T> result) => result.Code switch
    {
        AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
        AzoaErrorCodes.NotFound => NotFound(result),
        AzoaErrorCodes.Conflict => Conflict(result),
        AzoaErrorCodes.DependencyUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        FiatPayoutErrors.PayoutUnavailableCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        FiatPayoutErrors.PayoutBusyCode => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        _ => BadRequest(result),
    };

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
