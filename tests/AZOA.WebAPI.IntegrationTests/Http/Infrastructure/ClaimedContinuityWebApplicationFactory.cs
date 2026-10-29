using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>
/// The flow host with node-held (<c>Platform</c>) custody, so an onboarded member owns a real encrypted platform
/// wallet and the tenant status can report <c>ready</c>; a request carrying a Bearer token is validated by the real
/// JwtBearer handler, so the person acts with the login token their claim returned. See <c>Http/AGENTS.md</c> §claimed-continuity.
/// </summary>
public sealed class ClaimedContinuityWebApplicationFactory : HttpFlowWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // The wallet key is the base host's per-process TestWalletEncryptionKey; the claim token is returned
        // to the tenant (IntegrationTest only, allowed by the Program.cs boot guard) so the person side can claim.
        var settings = new Dictionary<string, string?>
        {
            ["CustodialAccounts:CustodyMode"] = "Platform",
            ["Tenant:ClaimInvites:ReturnTokenToTenant"] = "true",
        };
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));

        // Only a request that presents a Bearer token is validated for real; the operator and test principals
        // (header-selected, no token) keep reaching TestAuthHandler, including through Bearer-only policies.
        builder.ConfigureTestServices(services =>
        {
            services.Configure<PolicySchemeOptions>("TestAuthForward", options => options.ForwardDefaultSelector = context =>
                HasBearer(context) ? JwtBearerDefaults.AuthenticationScheme
                    : context.Request.Headers.ContainsKey("X-Api-Key") ? ApiKeyAuthenticationHandler.SchemeName : TestAuthHandler.SchemeName);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ForwardAuthenticate = null;
                options.ForwardChallenge = null;
                options.ForwardDefaultSelector = context => HasBearer(context) ? null : TestAuthHandler.SchemeName;
            });
        });
    }

    private static bool HasBearer(HttpContext context)
        => context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}
