using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OASIS.WebAPI.IntegrationTests.Factories;

/// <summary>
/// Test authentication handler that automatically authenticates every request
/// with a configurable set of claims. Used by integration tests to bypass JWT.
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

    public const string AuthHeaderName = "X-Test-Auth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(AuthHeaderName))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, DefaultAvatarId),
            new(ClaimTypes.Name, "testuser"),
            new(ClaimTypes.Email, "test@oasis.local"),
            new("sub", DefaultAvatarId)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
