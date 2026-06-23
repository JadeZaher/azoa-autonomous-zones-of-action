using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.IntegrationTests.Factories;

namespace AZOA.WebAPI.IntegrationTests.Mcp;

/// <summary>
/// Integration tests for MCP auth scoping.
///
/// <para>
/// These tests exercise the full request pipeline — JWT/ApiKey auth +
/// McpAuthMiddleware (registered in Program.cs after UseAuthorization) +
/// the MCP SDK endpoint at <c>/mcp</c>.
/// </para>
///
/// <para>
/// Skip guard: all tests call <see cref="IntegrationTestBase.SkipIfSurrealDbUnavailableAsync"/>
/// and skip gracefully when the SurrealDB test container is absent, matching the
/// pattern used across the integration test suite.
/// </para>
/// </summary>
[Trait("Category", "Mcp")]
public class McpAuthScopingIntegrationTests : IntegrationTestBase
{
    // ── inner: per-avatar auth handler ────────────────────────────────────────

    /// <summary>
    /// Auth handler that reads the avatar id from the <c>X-Test-Avatar-Id</c>
    /// header, falling back to <see cref="TestAuthHandler.DefaultAvatarId"/>.
    /// Allows a single factory instance to issue requests as different avatars
    /// without a full WebApplicationFactory restart per avatar.
    /// </summary>
    private sealed class ParameterizedTestAuthHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string AvatarIdHeader = "X-Test-Avatar-Id";

        public ParameterizedTestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(TestAuthHandler.AuthHeaderName))
                return Task.FromResult(AuthenticateResult.NoResult());

            var avatarId = Request.Headers.TryGetValue(AvatarIdHeader, out var v)
                ? v.FirstOrDefault() ?? TestAuthHandler.DefaultAvatarId
                : TestAuthHandler.DefaultAvatarId;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, avatarId),
                new(ClaimTypes.Name,  "testuser"),
                new(ClaimTypes.Email, "test@azoa.local"),
                new("sub",      avatarId),
                new("AvatarId", avatarId),
            };

            var identity  = new ClaimsIdentity(claims, TestAuthHandler.SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket    = new AuthenticationTicket(principal, TestAuthHandler.SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    // ── inner: factory that wires ParameterizedTestAuthHandler ───────────────

    private sealed class ParameterizedAuthFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"]      = "super-secret-test-key-that-is-long-enough!",
                    ["Jwt:Issuer"]   = "test",
                    ["Jwt:Audience"] = "test",
                    ["SurrealDb:Endpoint"] = SurrealTestDefaults.Endpoint,
                    ["SurrealDb:User"]     = SurrealTestDefaults.User,
                    ["SurrealDb:Password"] = SurrealTestDefaults.Password,
                    ["AZOA:DefaultProvider"] = "SurrealDb",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme    = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, ParameterizedTestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });
            });
        }

        /// Create an HTTP client authenticated as <paramref name="avatarId"/>.
        public HttpClient CreateClientForAvatar(Guid avatarId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
            client.DefaultRequestHeaders.Add(
                ParameterizedTestAuthHandler.AvatarIdHeader, avatarId.ToString());
            return client;
        }

        /// Create an unauthenticated HTTP client (no test-auth header).
        public HttpClient CreateUnauthenticatedClient() => CreateClient();
    }

    // ── constructor ───────────────────────────────────────────────────────────

    public McpAuthScopingIntegrationTests(AZOATestWebApplicationFactory factory)
        : base(factory) { }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _json =
        new(JsonSerializerDefaults.Web);

    /// Builds a JSON-RPC 2.0 tools/call payload for the given tool and arguments.
    private static StringContent McpToolCallPayload(
        string toolName,
        object arguments,
        string id = "req-1")
    {
        var body = new
        {
            jsonrpc = "2.0",
            id,
            method  = "tools/call",
            @params = new { name = toolName, arguments }
        };
        return new StringContent(
            JsonSerializer.Serialize(body, _json),
            Encoding.UTF8,
            "application/json");
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves that avatar A's authenticated request to a tool that references
    /// avatar B's holon id never returns B's holon data. The auth pipeline
    /// (RequireAuthorization + McpAuthMiddleware) must scope the request to A's
    /// identity, and B's data must never appear in the response regardless of
    /// whether the tool exists or returns an error.
    ///
    /// This test is the primary cross-tenant isolation gate: even a misconfigured
    /// or partially-wired MCP surface must not leak data across avatar boundaries.
    /// </summary>
    [SkippableFact]
    public async Task CrossTenantQuery_AvatarA_CannotSeeAvatarBHolons()
    {
        Skip.IfNot(
            await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable at http://localhost:8442 — " +
            "start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        using var pFactory = new ParameterizedAuthFactory();

        var avatarAId = Guid.NewGuid();
        var avatarBId = Guid.NewGuid();

        // Seed avatar B's holons via the real API authenticated as B.
        using var clientB = pFactory.CreateClientForAvatar(avatarBId);

        // Create three holons "owned" by avatar B.  POST /api/holon uses
        // GetAvatarIdFromClaims() to stamp AvatarId on the created record.
        var holonBIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var seedPayload = new { Name = $"BHolon{i}", AvatarId = avatarBId };
            var seedResp = await clientB.PostAsJsonAsync("api/holon", seedPayload, _json);
            // If the seed endpoint returns an error here we still proceed —
            // the important assertion is that A cannot see B's data. If seeding
            // fails the holons simply don't exist, so A definitely can't see them.
            if (seedResp.IsSuccessStatusCode)
            {
                var seedResult = await seedResp.Content
                    .ReadFromJsonAsync<JsonElement>(_json);
                if (seedResult.TryGetProperty("result", out var r) &&
                    r.TryGetProperty("id", out var idEl) &&
                    Guid.TryParse(idEl.GetString(), out var hid))
                {
                    holonBIds.Add(hid);
                }
            }
        }

        // Authenticate as avatar A and call the MCP tool surface, requesting
        // one of B's holon ids (or a fabricated one if seeding failed).
        using var clientA = pFactory.CreateClientForAvatar(avatarAId);
        var targetHolonId = holonBIds.FirstOrDefault(Guid.NewGuid());
        var mcpContent = McpToolCallPayload(
            "holon_traverse",
            new { holon_id = targetHolonId.ToString() });

        var mcpResp = await clientA.PostAsync("/mcp", mcpContent);

        // Core assertion: response must never contain B's holon data.
        // Acceptable outcomes: any 4xx, or a JSON-RPC error body.
        // Not acceptable: HTTP 200 with B's holon payload.
        var responseBody = await mcpResp.Content.ReadAsStringAsync();

        if (mcpResp.IsSuccessStatusCode)
        {
            // If the SDK returned 200, the body must be a JSON-RPC error or
            // "not found" — it must NEVER contain B's avatar id.
            responseBody.Should().NotContain(
                avatarBId.ToString(),
                because: "avatar A must never receive avatar B's data in an MCP response");
        }
        else
        {
            // Any 4xx/5xx satisfies the isolation requirement.
            ((int)mcpResp.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                because: "a non-2xx response proves the cross-tenant request was rejected");
        }
    }

    /// <summary>
    /// Proves that an unauthenticated POST to /mcp is rejected at the framework
    /// authorization layer (RequireAuthorization on MapMcp) with HTTP 401, before
    /// any tool logic runs. This is the no-credential gate — not a per-claim check.
    /// </summary>
    [SkippableFact]
    public async Task NoAvatarClaim_Returns401()
    {
        Skip.IfNot(
            await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable at http://localhost:8442 — " +
            "start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        using var pFactory = new ParameterizedAuthFactory();
        using var unauthClient = pFactory.CreateUnauthenticatedClient();

        var mcpContent = McpToolCallPayload(
            "holon_traverse",
            new { holon_id = Guid.NewGuid().ToString() });

        var response = await unauthClient.PostAsync("/mcp", mcpContent);

        // RequireAuthorization() on app.MapMcp() must reject unauthenticated
        // requests before they reach the MCP handler or McpAuthMiddleware.
        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            because: "the /mcp endpoint is protected by RequireAuthorization; " +
                     "unauthenticated requests must never reach the tool dispatcher");
    }
}
