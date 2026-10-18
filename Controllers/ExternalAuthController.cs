// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Backend-driven federated sign-in (Phase 1B, D18). Routes under <c>auth/oauth</c>.
/// </summary>
/// <remarks>
/// <para><b>Why backend-driven.</b> The client secret and the authorization-code
/// exchange never touch the browser. The SPA only ever navigates to <c>start</c> and
/// later POSTs an opaque code to <c>exchange</c>; everything between those two points
/// happens server-side.</para>
///
/// <para><b>state + PKCE are the framework's, not ours.</b> The challenge is handed to
/// the registered ASP.NET Core OAuth handler, which generates the <c>state</c>
/// (a DataProtection-encrypted correlation cookie) and the PKCE <c>code_verifier</c>,
/// and refuses the callback when either fails to round-trip. Hand-rolling that
/// validation is exactly what this design avoids.</para>
///
/// <para><b>IDOR rule</b> (mirrors <c>WalletAuthController</c>): every owned identity id
/// comes from the authenticated principal or a single-use token, never a request body.
/// The link flow needs a user id across a top-level redirect that carries no
/// Authorization header, so it threads one through a single-use LINK code minted from
/// an authenticated request — never a query parameter.</para>
/// </remarks>
[ApiController]
[Route("auth/oauth")]
public sealed class ExternalAuthController : ControllerBase
{
    /// <summary>Cookie scheme that carries the provider result between handler and callback.</summary>
    public const string ExternalScheme = "ExternalOAuth";

    private readonly IExternalLoginService _externalLogins;
    private readonly IOAuthExchangeCodeService _exchangeCodes;
    private readonly IUserLoginTokenIssuer _tokenIssuer;
    private readonly ExternalAuthOptions _options;
    private readonly ILogger<ExternalAuthController> _logger;

    public ExternalAuthController(
        IExternalLoginService externalLogins,
        IOAuthExchangeCodeService exchangeCodes,
        IUserLoginTokenIssuer tokenIssuer,
        IOptions<ExternalAuthOptions> options,
        ILogger<ExternalAuthController> logger)
    {
        _externalLogins = externalLogins;
        _exchangeCodes = exchangeCodes;
        _tokenIssuer = tokenIssuer;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Providers this node can actually complete a sign-in with.</summary>
    /// <remarks>
    /// Lets the frontend render only working buttons. A provider that is enabled but
    /// missing secrets never reaches here — the boot guard stops the host first.
    /// </remarks>
    [HttpGet("providers")]
    [AllowAnonymous]
    public ActionResult<AZOAResult<IReadOnlyList<string>>> Providers()
        => Ok(AZOAResult<IReadOnlyList<string>>.Success(_options.EnabledProviderNames(), "Success"));

    /// <summary>Begins a sign-in (or, with a link ticket, a provider attachment).</summary>
    [HttpGet("{provider}/start")]
    [AllowAnonymous]
    [EnableRateLimiting("financial")]
    public IActionResult Start(
        [FromRoute] string provider,
        [FromQuery] string? returnTo = null,
        [FromQuery] string? tenant = null,
        [FromQuery] string? ticket = null)
    {
        if (!TryResolveEnabledProvider(provider, out var resolved, out var failure))
            return failure;

        if (!TryNormalizeTenant(tenant, out var tenantId))
            return BadRequest(AZOAResult<string>.FailureWithCode(
                "The tenant parameter is not a valid identifier.", AzoaErrorCodes.InvalidRequest));

        var properties = new AuthenticationProperties
        {
            // Where the HANDLER returns to once it has validated state, exchanged the
            // code, and signed the result into the external cookie. Always a local
            // path — never anything a caller supplied.
            RedirectUri = Url.Action(nameof(Callback), new { provider = ExternalAuthProviderNames.ToRouteSegment(resolved) })
                ?? $"/auth/oauth/{ExternalAuthProviderNames.ToRouteSegment(resolved)}/callback",
        };

        // Carried inside the encrypted state, so the callback recovers them without
        // trusting anything in the returning query string.
        properties.Items[ItemReturnTo] = SafeReturnTo(returnTo);
        if (tenantId is not null)
            properties.Items[ItemTenant] = tenantId.Value.ToString("N");
        if (!string.IsNullOrWhiteSpace(ticket))
            properties.Items[ItemLinkTicket] = ticket.Trim();

        return Challenge(properties, ExternalAuthProviderNames.ToRouteSegment(resolved));
    }

    /// <summary>
    /// Lands here after the handler has validated <c>state</c>, verified PKCE, and
    /// exchanged the code. Resolves the user and hands the SPA a single-use code.
    /// </summary>
    [HttpGet("{provider}/callback")]
    [AllowAnonymous]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> Callback([FromRoute] string provider)
    {
        if (!ExternalAuthProviderNames.TryParse(provider, out var resolved))
            return RedirectToFrontend(error: "unsupported_provider", returnTo: null);

        var authenticated = await HttpContext.AuthenticateAsync(ExternalScheme);

        // The external cookie is a one-shot carrier: drop it whether or not the
        // sign-in succeeded, so a stale provider result can never be replayed.
        await HttpContext.SignOutAsync(ExternalScheme);

        if (!authenticated.Succeeded || authenticated.Principal is null)
        {
            // A failed state/PKCE round-trip arrives here as a plain failure. The user
            // gets a generic message; the reason goes to the log, not the query string.
            _logger.LogWarning(
                "Federated sign-in callback rejected for {Provider}: {Reason}",
                resolved,
                authenticated.Failure?.Message ?? "no external principal");
            return RedirectToFrontend(error: "sign_in_failed", returnTo: null);
        }

        var identity = ExternalIdentityReader.Read(resolved, authenticated.Principal);
        if (identity is null)
            return RedirectToFrontend(error: "sign_in_failed", returnTo: null);

        var items = authenticated.Properties?.Items ?? new Dictionary<string, string?>();
        items.TryGetValue(ItemReturnTo, out var returnTo);
        items.TryGetValue(ItemTenant, out var tenantRaw);
        items.TryGetValue(ItemLinkTicket, out var linkTicket);

        Guid? tenantId = Guid.TryParse(tenantRaw, out var parsedTenant) ? parsedTenant : null;

        return string.IsNullOrWhiteSpace(linkTicket)
            ? await CompleteSignInAsync(resolved, identity, tenantId, returnTo)
            : await CompleteLinkAsync(resolved, identity, linkTicket, returnTo);
    }

    /// <summary>Redeems the single-use code for the standard AZOA login JWT.</summary>
    [HttpPost("exchange")]
    [AllowAnonymous]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<ExternalAuthTokenResponse>>> Exchange(
        [FromBody] ExternalAuthExchangeRequest request)
    {
        var redeemed = await _exchangeCodes.RedeemAsync(
            request?.Code ?? string.Empty,
            OAuthExchangeCodePurpose.SESSION,
            HttpContext.RequestAborted);

        if (redeemed.IsError || redeemed.Result is null)
        {
            return Unauthorized(AZOAResult<ExternalAuthTokenResponse>.FailureWithCode(
                redeemed.Message, AzoaErrorCodes.InvalidRequest));
        }

        if (!Guid.TryParse(redeemed.Result.UserId, out var userId))
            return Unauthorized(AZOAResult<ExternalAuthTokenResponse>.FailureWithCode(
                "This sign-in link is invalid or has expired. Start again.",
                AzoaErrorCodes.InvalidRequest));

        // Re-read the user at redemption time rather than trusting anything cached in
        // the code row: a deactivation between callback and exchange must take effect.
        var user = await _externalLogins.ResolveBoundUserAsync(userId, HttpContext.RequestAborted);
        if (user.IsError || user.Result is null)
        {
            return Unauthorized(AZOAResult<ExternalAuthTokenResponse>.FailureWithCode(
                "This account is not available.", AzoaErrorCodes.Forbidden));
        }

        // The SAME issuer the password path uses — identical claims, lifetime, and
        // signing parameters. See UserLoginTokenIssuer.
        var token = _tokenIssuer.IssueLoginToken(user.Result);

        return Ok(AZOAResult<ExternalAuthTokenResponse>.Success(
            new ExternalAuthTokenResponse
            {
                Token = token,
                UserId = user.Result.Id,
                Username = user.Result.Username,
                Email = user.Result.Email,
                Provider = ExternalAuthProviderNames.ToRouteSegment(redeemed.Result.Provider),
                ReturnTo = SafeReturnTo(redeemed.Result.ReturnTo),
            },
            "Signed in."));
    }

    /// <summary>
    /// Mints the single-use ticket that authorises attaching a provider to THIS user.
    /// </summary>
    /// <remarks>
    /// Rule 1's "explicit user action from an already-authenticated session" lives here:
    /// the ticket can only be obtained with a valid login JWT, and the subsequent
    /// redirect carries the ticket rather than a user id, so a caller can never name
    /// an account other than their own.
    /// </remarks>
    [HttpPost("{provider}/link/start")]
    [Authorize(Policy = "FirstPartyLogin")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<ExternalAuthLinkTicketResponse>>> StartLink(
        [FromRoute] string provider,
        [FromQuery] string? tenant = null)
    {
        if (!TryResolveEnabledProvider(provider, out var resolved, out var failure))
            return failure;

        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(AZOAResult<ExternalAuthLinkTicketResponse>.FailureWithCode(
                "Sign in first.", AzoaErrorCodes.Forbidden));

        if (!TryNormalizeTenant(tenant, out var tenantId))
            return BadRequest(AZOAResult<ExternalAuthLinkTicketResponse>.FailureWithCode(
                "The tenant parameter is not a valid identifier.", AzoaErrorCodes.InvalidRequest));

        var ticket = await _exchangeCodes.MintAsync(
            OAuthExchangeCodePurpose.LINK,
            userId,
            resolved,
            tenantId,
            KycPersonKeyDeriver.ScopeValue(tenantId),
            returnTo: null,
            HttpContext.RequestAborted);

        if (ticket.IsError || ticket.Result is null)
            return StatusCode(503, AZOAResult<ExternalAuthLinkTicketResponse>.FailureWithCode(
                ticket.Message, AzoaErrorCodes.DependencyUnavailable));

        return Ok(AZOAResult<ExternalAuthLinkTicketResponse>.Success(
            new ExternalAuthLinkTicketResponse
            {
                Ticket = ticket.Result,
                StartUrl = $"/auth/oauth/{ExternalAuthProviderNames.ToRouteSegment(resolved)}/start",
            },
            "Ticket minted."));
    }

    /// <summary>Providers currently attached to the authenticated user.</summary>
    [HttpGet("links")]
    [Authorize]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<ExternalAuthLinkResponse>>>> Links()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(AZOAResult<IReadOnlyList<ExternalAuthLinkResponse>>.FailureWithCode(
                "Sign in first.", AzoaErrorCodes.Forbidden));

        var links = await _externalLogins.ListLinksAsync(userId, HttpContext.RequestAborted);
        if (links.IsError)
            return StatusCode(503, AZOAResult<IReadOnlyList<ExternalAuthLinkResponse>>.FailureWithCode(
                links.Message, AzoaErrorCodes.DependencyUnavailable));

        var projected = (links.Result ?? [])
            .Select(link => new ExternalAuthLinkResponse
            {
                Provider = ExternalAuthProviderNames.ToRouteSegment(link.Provider),
                EmailAtLink = link.EmailAtLink,
                DisplayNameAtLink = link.DisplayNameAtLink,
                LinkedAt = link.LinkedAt,
                LastLoginAt = link.LastLoginAt,
            })
            .ToList();

        return Ok(AZOAResult<IReadOnlyList<ExternalAuthLinkResponse>>.Success(projected, "Success"));
    }

    /// <summary>Detaches a provider from the authenticated user.</summary>
    [HttpDelete("links/{provider}")]
    [Authorize(Policy = "FirstPartyLogin")]
    public async Task<ActionResult<AZOAResult<bool>>> Unlink(
        [FromRoute] string provider,
        [FromQuery] string? tenant = null)
    {
        if (!ExternalAuthProviderNames.TryParse(provider, out var resolved))
            return BadRequest(AZOAResult<bool>.FailureWithCode(
                "Unsupported provider.", AzoaErrorCodes.InvalidRequest));

        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(AZOAResult<bool>.FailureWithCode("Sign in first.", AzoaErrorCodes.Forbidden));

        if (!TryNormalizeTenant(tenant, out var tenantId))
            return BadRequest(AZOAResult<bool>.FailureWithCode(
                "The tenant parameter is not a valid identifier.", AzoaErrorCodes.InvalidRequest));

        var result = await _externalLogins.UnlinkAsync(resolved, userId, tenantId, HttpContext.RequestAborted);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    internal const string ItemReturnTo = "azoa.returnTo";
    internal const string ItemTenant = "azoa.tenant";
    internal const string ItemLinkTicket = "azoa.linkTicket";

    private async Task<IActionResult> CompleteSignInAsync(
        ExternalAuthProvider provider,
        ExternalIdentity identity,
        Guid? tenantId,
        string? returnTo)
    {
        var user = await _externalLogins.ResolveOrCreateAsync(identity, tenantId, HttpContext.RequestAborted);
        if (user.IsError && user.Code == SelfRegistrationPolicy.DisabledCode)
            return RedirectToFrontend(error: "registration_closed", returnTo: returnTo);
        if (user.IsError || user.Result is null)
            return RedirectToFrontend(error: "sign_in_failed", returnTo: returnTo);

        var code = await _exchangeCodes.MintAsync(
            OAuthExchangeCodePurpose.SESSION,
            user.Result.Id,
            provider,
            tenantId,
            KycPersonKeyDeriver.ScopeValue(tenantId),
            SafeReturnTo(returnTo),
            HttpContext.RequestAborted);

        return code.IsError || code.Result is null
            ? RedirectToFrontend(error: "sign_in_failed", returnTo: returnTo)
            : RedirectToFrontend(code: code.Result, returnTo: returnTo);
    }

    private async Task<IActionResult> CompleteLinkAsync(
        ExternalAuthProvider provider,
        ExternalIdentity identity,
        string linkTicket,
        string? returnTo)
    {
        var redeemed = await _exchangeCodes.RedeemAsync(
            linkTicket, OAuthExchangeCodePurpose.LINK, HttpContext.RequestAborted);

        if (redeemed.IsError || redeemed.Result is null
            || !Guid.TryParse(redeemed.Result.UserId, out var userId))
        {
            return RedirectToFrontend(error: "link_failed", returnTo: returnTo);
        }

        if (redeemed.Result.Provider != provider)
        {
            // The ticket authorised a different provider. Refuse rather than attach the
            // one that happened to come back.
            return RedirectToFrontend(error: "link_failed", returnTo: returnTo);
        }

        Guid? tenantId = Guid.TryParse(redeemed.Result.TenantId, out var parsed) ? parsed : null;

        var linked = await _externalLogins.LinkToUserAsync(
            identity, userId, tenantId, HttpContext.RequestAborted);

        return linked.IsError
            ? RedirectToFrontend(error: "link_conflict", returnTo: returnTo)
            : RedirectToFrontend(linked: ExternalAuthProviderNames.ToRouteSegment(provider), returnTo: returnTo);
    }

    /// <summary>
    /// Builds the ONE redirect target this controller will ever send a browser to.
    /// </summary>
    /// <remarks>
    /// The origin comes from configuration and the path is relative and validated, so
    /// the callback can never be turned into an open redirect that leaks the exchange
    /// code to an attacker-chosen host.
    /// </remarks>
    private IActionResult RedirectToFrontend(
        string? code = null,
        string? error = null,
        string? linked = null,
        string? returnTo = null)
    {
        var basePath = string.IsNullOrWhiteSpace(_options.FrontendBaseUrl)
            ? string.Empty
            : _options.FrontendBaseUrl.TrimEnd('/');
        var path = string.IsNullOrWhiteSpace(_options.FrontendCallbackPath)
            ? "/callback"
            : _options.FrontendCallbackPath;

        var query = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(code)) query.Add($"code={Uri.EscapeDataString(code)}");
        if (!string.IsNullOrWhiteSpace(error)) query.Add($"error={Uri.EscapeDataString(error)}");
        if (!string.IsNullOrWhiteSpace(linked)) query.Add($"linked={Uri.EscapeDataString(linked)}");

        var safeReturnTo = SafeReturnTo(returnTo);
        if (!string.IsNullOrWhiteSpace(safeReturnTo))
            query.Add($"returnTo={Uri.EscapeDataString(safeReturnTo)}");

        var target = $"{basePath}{path}";
        if (query.Count > 0) target += "?" + string.Join("&", query);

        return Redirect(target);
    }

    /// <summary>
    /// Accepts only a same-site relative path. Mirrors the login page's own check;
    /// anything absolute, protocol-relative, or backslash-bearing is discarded.
    /// </summary>
    internal static string? SafeReturnTo(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var value = candidate.Trim();
        return value.StartsWith('/')
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Contains('\\', StringComparison.Ordinal)
                ? value
                : null;
    }

    private bool TryResolveEnabledProvider(
        string? routeSegment,
        out ExternalAuthProvider provider,
        out ActionResult failure)
    {
        failure = BadRequest(AZOAResult<string>.FailureWithCode(
            "That sign-in provider is not available on this node.", AzoaErrorCodes.InvalidRequest));

        if (!ExternalAuthProviderNames.TryParse(routeSegment, out provider))
            return false;

        var enabled = _options.EnabledProviderNames();
        return enabled.Contains(ExternalAuthProviderNames.ToRouteSegment(provider), StringComparer.Ordinal);
    }

    private static bool TryNormalizeTenant(string? tenant, out Guid? tenantId)
    {
        tenantId = null;
        if (string.IsNullOrWhiteSpace(tenant)) return true;
        if (!Guid.TryParse(tenant.Trim(), out var parsed)) return false;
        tenantId = parsed == Guid.Empty ? null : parsed;
        return true;
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst("UserId")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }
}
