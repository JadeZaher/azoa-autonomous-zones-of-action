// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// The sponsorship moment: a consumer that has already decided the amounts and the rate calls
/// this, the sponsor's funds reach the project on chain, and the sponsor's units are either
/// theirs immediately or held for a window — all in one atomic group.
/// </summary>
/// <remarks>
/// <para><b>Consent is checked twice, on purpose.</b> Once here, before any side effect, and
/// again inside <c>KeyCustodyService</c> immediately before the key decrypt. The deep seam
/// alone only fires if something actually reaches custody, so a short-circuiting rail would
/// slip past it; the edge check alone cannot see what is ultimately signed. Both, or neither is
/// a control. This is the same arrangement <c>AllocationController</c> uses, for the same
/// reason.</para>
/// <para><b>IDOR:</b> the person an exchange is for is the route value. No body field can
/// redirect a sponsorship to a different account.</para>
/// </remarks>
[ApiController]
[Route("api/exchange")]
[Authorize]
public sealed class ExchangeController : ControllerBase
{
    /// <summary>Longest idempotency key this route will look up. Bounds the path segment.</summary>
    private const int MaxIdempotencyKeyLength = 128;

    private readonly IReserveExchangeManager _exchange;
    private readonly ITenantConsentGate? _consentGate;
    private readonly SigningAdmissionAuthorityFactory? _signing;

    /// <param name="consentGate">
    /// The live consent check (registered in <c>Program.cs</c>). Optional only so the type still
    /// constructs without a container; a request that reaches the exchange with no gate is
    /// REFUSED, never waved through.
    /// </param>
    /// <param name="signing">Captures the authenticated API-key credential an effectful exchange is journaled under.</param>
    public ExchangeController(IReserveExchangeManager exchange, ITenantConsentGate? consentGate = null,
        SigningAdmissionAuthorityFactory? signing = null)
    {
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        _consentGate = consentGate;
        _signing = signing;
    }

    /// <summary>
    /// Runs one exchange for the person named by the route. Idempotent on the
    /// <c>Idempotency-Key</c> header, partitioned by the caller's API key, so a redelivered
    /// request never sponsors twice.
    /// </summary>
    [HttpPost("{userId:guid}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<ExchangeReceipt>>> Exchange(
        Guid userId,
        [FromBody] ExchangeRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(AZOAResult<ExchangeReceipt>.FailureWithCode(
                "An exchange request is required.", AzoaErrorCodes.InvalidRequest));
        }

        var identity = ResolveCaller(userId, idempotencyKey);
        if (identity.Error is not null)
            return identity.Error!;

        var caller = identity.Caller!;

        // A plain preview claims nothing and moves nothing, so it is exempt from the consent check.
        // A preview carrying a release mandate discloses the participant's wallet and readiness, so it is gated.
        if (!request.PreviewOnly || request.ReleaseMandate is not null)
        {
            var consentRefusal = await RequireLiveConsentAsync(userId, caller.ActingTenantId, ct);
            if (consentRefusal is not null)
                return consentRefusal;
        }

        if (!request.PreviewOnly)
        {
            var captured = _signing is null ? null : await _signing.CaptureCredentialAsync(User, ct);
            if (captured is null || captured.IsError || captured.Result is null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "The authenticated API-key credential could not be captured.", AzoaErrorCodes.Forbidden));
            }

            caller = new ExchangeCallerContext
            {
                ApiKeyId = caller.ApiKeyId,
                CallerUserId = caller.CallerUserId,
                ActingTenantId = caller.ActingTenantId,
                TargetUserId = caller.TargetUserId,
                ClientIdempotencyKey = caller.ClientIdempotencyKey,
                Credential = captured.Result,
            };
        }

        var result = await _exchange.ExchangeAsync(request, caller, ct);
        return Translate(result);
    }

    /// <summary>
    /// The receipt for one exchange — the read surface a consumer reconciles against. Scoped to
    /// the API key and the identity that ran it.
    /// </summary>
    [HttpGet("receipt/{idempotencyKey}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<ExchangeReceipt>>> GetReceipt(
        string idempotencyKey, CancellationToken ct)
    {
        var principal = ReadPrincipal();
        if (principal.Error is not null)
            return principal.Error!;

        if (!IsAcceptableIdempotencyKey(idempotencyKey))
        {
            return BadRequest(AZOAResult<ExchangeReceipt>.FailureWithCode(
                $"An idempotency key of 1-{MaxIdempotencyKeyLength} characters is required.",
                AzoaErrorCodes.InvalidRequest));
        }

        var result = await _exchange.GetReceiptAsync(
            new ExchangeReceiptQuery
            {
                ApiKeyId = principal.ApiKeyId,
                CallerUserId = principal.CallerUserId,
                ClientIdempotencyKey = idempotencyKey.Trim(),
            },
            ct);

        return Translate(result);
    }

    // ── Principal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The caller context an exchange runs under, or the refusal that stopped it. Every field
    /// comes from the authenticated principal and the route; none comes from the body.
    /// </summary>
    private (ExchangeCallerContext? Caller, ActionResult<AZOAResult<ExchangeReceipt>>? Error) ResolveCaller(
        Guid userId, string? idempotencyKey)
    {
        var principal = ReadPrincipal();
        if (principal.Error is not null)
            return (null, principal.Error);

        if (!IsAcceptableIdempotencyKey(idempotencyKey))
        {
            return (null, BadRequest(AZOAResult<ExchangeReceipt>.FailureWithCode(
                "An Idempotency-Key header of 1-" + MaxIdempotencyKeyLength + " characters is required. "
                + "Two identical sponsorships are a normal thing to want, so the node will not guess "
                + "whether a repeat is a retry or a second one.",
                AzoaErrorCodes.InvalidRequest)));
        }

        // The acting tenant comes from the principal alone; no request field participates. An
        // API key is a machine credential acting on somebody else's account, so it is ALWAYS
        // treated as acting for the route-supplied person and the consent check can never be
        // skipped by a principal that merely lacks a claim.
        var claimedTenantId = User.GetActingTenantId();
        if (claimedTenantId is { } fromClaim && fromClaim != principal.CallerUserId)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "The exchange principal names two different acting tenants.",
                    AzoaErrorCodes.Forbidden)));
        }

        return (new ExchangeCallerContext
        {
            ApiKeyId = principal.ApiKeyId,
            CallerUserId = principal.CallerUserId,
            ActingTenantId = principal.CallerUserId,
            TargetUserId = userId,
            ClientIdempotencyKey = idempotencyKey!.Trim(),
        }, null);
    }

    /// <summary>
    /// The API-key identity behind this call, or the refusal. An exchange moves a person's
    /// funds on a consumer's instruction, so only an API-key principal carrying the exchange
    /// scope reaches the manager.
    /// </summary>
    private (Guid ApiKeyId, Guid CallerUserId, ActionResult<AZOAResult<ExchangeReceipt>>? Error) ReadPrincipal()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return (Guid.Empty, Guid.Empty, Unauthorized(
                AZOAResult<ExchangeReceipt>.FailureWithCode("Invalid token.", AzoaErrorCodes.Forbidden)));
        }

        if (!string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return (Guid.Empty, Guid.Empty, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "An exchange requires an API-key principal.", AzoaErrorCodes.Forbidden)));
        }

        if (!User.HasScope(ReserveExchangeManager.ExchangeSigningScope))
        {
            return (Guid.Empty, Guid.Empty, StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<ExchangeReceipt>.FailureWithCode(
                    $"Caller lacks the '{ReserveExchangeManager.ExchangeSigningScope}' scope required to exchange.",
                    AzoaErrorCodes.Forbidden)));
        }

        if (!Guid.TryParse(User.FindFirst("ApiKeyId")?.Value, out var apiKeyId) || apiKeyId == Guid.Empty
            || !TryGetUserId(out var callerUserId))
        {
            return (Guid.Empty, Guid.Empty, Unauthorized(
                AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "Invalid API-key identity.", AzoaErrorCodes.Forbidden)));
        }

        return (apiKeyId, callerUserId, null);
    }

    /// <summary>
    /// Refuses the exchange unless the person has a LIVE consent grant to this tenant covering
    /// the scope the exchange will sign under.
    /// </summary>
    /// <remarks>
    /// The probe is the very same <see cref="ITenantConsentGate"/> the custody seam calls, fed
    /// the same <see cref="SigningContext"/> shape, so the two checks cannot disagree about what
    /// "consented" means and the refusal lands in the same consent audit trail.
    /// </remarks>
    private async Task<ActionResult<AZOAResult<ExchangeReceipt>>?> RequireLiveConsentAsync(
        Guid targetUserId, Guid actingTenantId, CancellationToken ct)
    {
        var scope = ReserveExchangeManager.ExchangeSigningScope;
        if (!AzoaScopes.IsScopeValidForOperation(ReserveExchangeManager.ChainOperationType, scope))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "An exchange has no consent-checkable signing scope on this node.",
                    AzoaErrorCodes.Forbidden));
        }

        if (_consentGate is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "Consent cannot be verified; the exchange was refused.",
                    AzoaErrorCodes.DependencyUnavailable));
        }

        var probe = new SigningContext(targetUserId, Guid.Empty, IsPlatform: false)
            .ActingAs(actingTenantId, scope, targetUserId);

        var consent = await _consentGate.EnsureAllowedAsync(probe, ct);

        // Deliberately NOT a conditional expression. Its natural type would be bare
        // ActionResult, so the null arm reached ActionResult<T>'s implicit operator as a null
        // ActionResult and threw ArgumentNullException - meaning every consent-ALLOWED exchange
        // 500'd, while the refusal path this method was written for worked. Returning null from
        // its own statement types it as the nullable ActionResult<T>? and never converts.
        if (!consent.IsError)
            return null;

        return StatusCode(StatusCodes.Status403Forbidden,
            AZOAResult<ExchangeReceipt>.FailureWithCode(consent.Message, AzoaErrorCodes.Forbidden));
    }

    private static bool IsAcceptableIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var trimmed = key.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
            return false;

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_' or ':' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private ActionResult<AZOAResult<ExchangeReceipt>> Translate(AZOAResult<ExchangeReceipt> result)
    {
        if (!result.IsError)
            return Ok(result);

        // Fail-closed identity readiness surfaces a KYC_FORBIDDEN: prefix.
        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        return result.Code switch
        {
            AzoaErrorCodes.NotFound => NotFound(result),
            AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
            AzoaErrorCodes.OperationNotAllowed => StatusCode(StatusCodes.Status409Conflict, result),
            // A refusal that provably moved nothing is NOT a conflict. 409 says "this may
            // already have happened" and a consumer holding the seller's units must keep them
            // locked on it; 422 says "we read it and said no", which is safe to unlock on.
            ConsumerSafeErrorCodes.Declined =>
                StatusCode(StatusCodes.Status422UnprocessableEntity, result),
            AzoaErrorCodes.DependencyUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            ConsumerSafeErrorCodes.RailUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            ConsumerSafeErrorCodes.RailBusy => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            ConsumerSafeErrorCodes.SettlementPending => Accepted(result),
            // A place in the buy-back queue is an ACCEPTANCE, not a refusal. Nothing moved and
            // the units must STAY set aside, so this must never be 422 — that is the one status
            // a consumer may release a seller's units on, and releasing them destroys the place.
            BuyBackLiquidityPolicy.QueuedForFillCode => Accepted(result),
            _ => BadRequest(result),
        };
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.FindFirst("UserId")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out userId) && userId != Guid.Empty;
    }
}
