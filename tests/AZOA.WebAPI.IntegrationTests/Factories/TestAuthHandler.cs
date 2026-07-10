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
/// Avatar id selection:
///   - <see cref="AvatarHeaderName"/> ("X-Test-Avatar-Id") — when present and a
///     valid Guid, that avatar id is injected into NameIdentifier / sub claims.
///     This is how multi-avatar IDOR tests authenticate as a non-default user.
///   - Otherwise <see cref="DefaultAvatarId"/> is used. Backwards-compatible
///     with every existing test that just sends "X-Test-Auth: true".
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestScheme";
    public const string DefaultAvatarId = "a1111111-1111-1111-1111-111111111111";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    public const string AuthHeaderName   = "X-Test-Auth";
    public const string AvatarHeaderName = "X-Test-Avatar-Id";
    public const string DappRoleHeaderName = "X-Test-Dapp-Role";
    // avatar-dapp-rbac: when "true", stamp operator:admin + role=Admin so a test can
    // exercise operator-gated surfaces (the Operator policy + the role-assign path).
    public const string OperatorHeaderName = "X-Test-Operator";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(AuthHeaderName))
            return Task.FromResult(AuthenticateResult.NoResult());

        var avatarId = ResolveAvatarId();

        var dappRole = ResolveDappRole();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, avatarId),
            new(ClaimTypes.Name, "testuser"),
            new(ClaimTypes.Email, "test@azoa.local"),
            new("sub", avatarId),
            // Real JWTs carry an explicit "AvatarId" claim; some controllers
            // (e.g. AvatarNFTController.MintAvatarNFT) read it directly via
            // Guid.Parse(FindFirst("AvatarId")). Emit it so the test principal
            // matches production claim shape (absence caused Guid.Parse("") 500s).
            new("AvatarId", avatarId),
            new("dapp_role", dappRole)
        };

        if (Request.Headers.TryGetValue(OperatorHeaderName, out var op)
            && string.Equals(op.ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim("scope", AZOA.WebAPI.Core.AzoaScopes.Operator));
            claims.Add(new Claim("role", "Admin"));
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string ResolveAvatarId()
    {
        if (Request.Headers.TryGetValue(AvatarHeaderName, out var raw)
            && Guid.TryParse(raw.ToString(), out var parsed))
        {
            return parsed.ToString();
        }
        return DefaultAvatarId;
    }

    private string ResolveDappRole()
    {
        if (Request.Headers.TryGetValue(DappRoleHeaderName, out var raw))
            return AZOA.WebAPI.Core.AzoaDappRoles.Normalize(raw.ToString());

        // FOLLOW-UP (avatar-dapp-rbac review): defaulting to the most-privileged role
        // masks DappDevelop/DappManage gate regressions. Flipping to User cascades into
        // ~57 Seed* call sites + 15 direct authoring POSTs (base Client seeds via this),
        // so it's deferred to a dedicated churn track rather than done here.
        return AZOA.WebAPI.Core.AzoaDappRoles.Manager;
    }
}
