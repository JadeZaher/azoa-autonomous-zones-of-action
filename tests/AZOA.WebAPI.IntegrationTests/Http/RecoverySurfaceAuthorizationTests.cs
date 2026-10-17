using System.Net;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// Who may reach the recovery and reconciliation reads, through the real authentication
/// schemes and policies. Feature behavior behind each route belongs to its owning lane's flows.
/// See <c>Http/AGENTS.md</c> §flows.
/// </summary>
public sealed class RecoverySurfaceAuthorizationTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    public static TheoryData<string, string, string> Matrix => new()
    {
        // route, caller, expected outcome ("allowed" = the route exists, authorized, and answered
        // without a server fault; an unconfigured feature is a 409, never a 404 or 5xx).
        // These policies authenticate with the bearer scheme only, so an API key is never even
        // evaluated: it is challenged (401), exactly like an anonymous caller.
        { "api/ethereum/recovery/Devnet", "anonymous", "401" },
        { "api/ethereum/recovery/Devnet", "tenant-key", "401" },
        { "api/ethereum/recovery/Devnet", "login", "allowed" },
        { "api/operator/ethereum/recovery/Devnet", "login", "403" },
        { "api/operator/ethereum/recovery/Devnet", "operator", "allowed" },
        { "api/operator/wallet-identities", "tenant-key", "401" },
        { "api/operator/wallet-identities", "login", "403" },
        { "api/operator/wallet-identities", "operator", "allowed" },
        { "api/operator/hosted-sessions/reconciliation", "login", "403" },
        { "api/operator/hosted-sessions/reconciliation", "operator", "allowed" },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Recovery_reads_admit_only_their_intended_principal(string route, string caller, string expected)
    {
        using var tenant = caller == "tenant-key" ? await OnboardTenantAsync() : null;
        var client = caller switch
        {
            "anonymous" => Anonymous,
            "tenant-key" => tenant!.Client,
            "login" => Client,
            _ => Operator,
        };

        using var response = await client.GetAsync(route);

        var body = await response.Content.ReadAsStringAsync();
        if (expected == "allowed")
        {
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound }
                .Should().NotContain(response.StatusCode, body);
            ((int)response.StatusCode).Should().BeLessThan(500, body);
        }
        else
            response.StatusCode.Should().Be((HttpStatusCode)int.Parse(expected), body);
    }
}
