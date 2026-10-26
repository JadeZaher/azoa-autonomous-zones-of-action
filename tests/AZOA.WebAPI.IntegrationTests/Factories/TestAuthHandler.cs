using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.IntegrationTests.Factories;

/// <summary>
/// Test authentication handler that automatically authenticates every request
/// with a configurable set of claims. Used by integration tests to bypass JWT.
///
/// User id selection:
///   - <see cref="UserHeaderName"/> ("X-Test-User-Id") — when present and a
///     valid Guid, that user id is injected into NameIdentifier / sub claims.
///     This is how multi-user IDOR tests authenticate as a non-default user.
///   - Otherwise <see cref="DefaultUserId"/> is used. Backwards-compatible
///     with every existing test that just sends "X-Test-Auth: true".
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestScheme";
    public const string DefaultUserId = "a1111111-1111-1111-1111-111111111111";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    public const string AuthHeaderName   = "X-Test-Auth";
    public const string UserHeaderName = "X-Test-User-Id";
    public const string DappRoleHeaderName = "X-Test-Dapp-Role";
    // user-dapp-rbac: when "true", stamp operator:admin + role=Admin so a test can
    // exercise operator-gated surfaces (the Operator policy + the role-assign path).
    public const string OperatorHeaderName = "X-Test-Operator";
    public const string NodeGovernHeaderName = "X-Test-Node-Govern";
    public const string OperatorOnlyHeaderName = "X-Test-Operator-Only";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(AuthHeaderName))
            return Task.FromResult(AuthenticateResult.NoResult());

        var operatorRequested = Request.Headers.TryGetValue(OperatorHeaderName, out var operatorHeader)
            && string.Equals(operatorHeader.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        // A real node-operator token always names the reserved operator subject (OnTokenValidated enforces it), so an
        // operator principal defaults to it; operator routes resolve their actor from it (Services/NodeAdmin/AGENTS.md §operator-actor).
        var userId = ResolveUserId(operatorRequested
            ? AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId.ToString()
            : DefaultUserId);

        var dappRole = ResolveDappRole();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, "testuser"),
            new(ClaimTypes.Email, "test@azoa.local"),
            new("sub", userId),
            // Real JWTs carry an explicit "UserId" claim; some controllers
            // (e.g. UserNFTController.MintUserNFT) read it directly via
            // Guid.Parse(FindFirst("UserId")). Emit it so the test principal
            // matches production claim shape (absence caused Guid.Parse("") 500s).
            new("UserId", userId),
            new("dapp_role", dappRole)
        };

        var isOperator = Request.Headers.TryGetValue(OperatorHeaderName, out var op)
            && string.Equals(op.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var isOperatorOnly = Request.Headers.TryGetValue(OperatorOnlyHeaderName, out var opOnly)
            && string.Equals(opOnly.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var isNodeGovernor = Request.Headers.TryGetValue(NodeGovernHeaderName, out var node)
            && string.Equals(node.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        // Same time claims a validated login JWT carries (UserLoginTokenIssuer); see AGENTS.md §signing-principal.
        var authenticatedAt = DateTimeOffset.UtcNow;
        claims.Add(new Claim(AZOA.WebAPI.Core.AzoaClaims.AuthTime, UnixSeconds(authenticatedAt)));
        claims.Add(new Claim("iat", UnixSeconds(authenticatedAt)));
        claims.Add(new Claim("exp", UnixSeconds(authenticatedAt.Add(TokenLifetime))));

        if (isOperator)
        {
            claims.Add(new Claim(
                AZOA.WebAPI.Core.AzoaClaims.TokenUse,
                AZOA.WebAPI.Core.AzoaClaims.TokenUseNodeOperator));
            claims.Add(new Claim("scope", AZOA.WebAPI.Core.AzoaScopes.Operator));
            claims.Add(new Claim("role", "Admin"));
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }
        else
        {
            claims.Add(new Claim(
                AZOA.WebAPI.Core.AzoaClaims.TokenUse,
                AZOA.WebAPI.Core.AzoaClaims.TokenUseLogin));
        }

        if ((isOperator && !isOperatorOnly) || isNodeGovernor)
            claims.Add(new Claim("scope", AZOA.WebAPI.Core.AzoaScopes.NodeGovern));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    private static string UnixSeconds(DateTimeOffset instant)
        => instant.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string ResolveUserId(string fallback)
    {
        if (Request.Headers.TryGetValue(UserHeaderName, out var raw)
            && Guid.TryParse(raw.ToString(), out var parsed))
        {
            return parsed.ToString();
        }
        return fallback;
    }

    private string ResolveDappRole()
    {
        if (Request.Headers.TryGetValue(DappRoleHeaderName, out var raw))
            return AZOA.WebAPI.Core.AzoaDappRoles.Normalize(raw.ToString());

        // FOLLOW-UP (user-dapp-rbac review): defaulting to the most-privileged role
        // masks DappDevelop/DappManage gate regressions. Flipping to User cascades into
        // ~57 Seed* call sites + 15 direct authoring POSTs (base Client seeds via this),
        // so it's deferred to a dedicated churn track rather than done here.
        return AZOA.WebAPI.Core.AzoaDappRoles.Manager;
    }
}
