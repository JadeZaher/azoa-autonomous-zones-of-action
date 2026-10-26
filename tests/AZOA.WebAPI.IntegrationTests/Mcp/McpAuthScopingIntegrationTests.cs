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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

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
    // ── inner: per-user auth handler ────────────────────────────────────────

    /// <summary>
    /// Auth handler that reads the user id from the <c>X-Test-User-Id</c>
    /// header, falling back to <see cref="TestAuthHandler.DefaultUserId"/>.
    /// Allows a single factory instance to issue requests as different users
    /// without a full WebApplicationFactory restart per user.
    /// </summary>
    private sealed class ParameterizedTestAuthHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string UserIdHeader = "X-Test-User-Id";

        public ParameterizedTestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(TestAuthHandler.AuthHeaderName))
                return Task.FromResult(AuthenticateResult.NoResult());

            var userId = Request.Headers.TryGetValue(UserIdHeader, out var v)
                ? v.FirstOrDefault() ?? TestAuthHandler.DefaultUserId
                : TestAuthHandler.DefaultUserId;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId),
                new(ClaimTypes.Name,  "testuser"),
                new(ClaimTypes.Email, "test@azoa.local"),
                new("sub",      userId),
                new("UserId", userId),
                new("dapp_role", AZOA.WebAPI.Core.AzoaDappRoles.Manager),
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
        private readonly string _testNamespace = $"itest{Guid.NewGuid():N}";

        /// <summary>Bootstraps this independent host's namespace before hosted services query it.</summary>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            SurrealTestSchema.BootstrapAsync(
                    _testNamespace,
                    "admin_bootstrap_state",
                    "saga_steps",
                    "user",
                    "holon",
                    "holon_type_registry",
                    "node_governance_parameters")
                .GetAwaiter()
                .GetResult();
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("OpenTelemetry:ExportEnabled", "false");

            // Surreal settings go into HOST configuration: Program.cs binds the
            // section eagerly (AddSurrealForgeSdk needs the endpoint at
            // registration time), which happens before any
            // ConfigureAppConfiguration delegate here runs. See
            // AZOATestWebApplicationFactory for the full note.
            builder.UseSetting("SurrealDb:Endpoint",  SurrealTestDefaults.Endpoint);
            builder.UseSetting("SurrealDb:User",      SurrealTestDefaults.User);
            builder.UseSetting("SurrealDb:Password",  SurrealTestDefaults.Password);
            builder.UseSetting("SurrealDb:Namespace", _testNamespace);
            builder.UseSetting("SurrealDb:Database",  AZOATestWebApplicationFactory.TestDatabase);

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"]      = "super-secret-test-key-that-is-long-enough!",
                    ["Jwt:Issuer"]   = "test",
                    ["Jwt:Audience"] = "test",
                    ["OpenTelemetry:ExportEnabled"] = "false",
                    ["SurrealDb:Endpoint"] = SurrealTestDefaults.Endpoint,
                    ["SurrealDb:User"]     = SurrealTestDefaults.User,
                    ["SurrealDb:Password"] = SurrealTestDefaults.Password,
                    ["SurrealDb:Namespace"] = _testNamespace,
                    ["SurrealDb:Database"]  = AZOATestWebApplicationFactory.TestDatabase,
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

        /// Create an HTTP client authenticated as <paramref name="userId"/>.
        public HttpClient CreateClientForUser(Guid userId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
            client.DefaultRequestHeaders.Add(
                ParameterizedTestAuthHandler.UserIdHeader, userId.ToString());
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
            System.Text.Encoding.UTF8,
            "application/json");
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves that user A's authenticated request to a tool that references
    /// user B's holon id never returns B's holon data. The auth pipeline
    /// (RequireAuthorization + McpAuthMiddleware) must scope the request to A's
    /// identity, and B's data must never appear in the response regardless of
    /// whether the tool exists or returns an error.
    ///
    /// This test is the primary cross-tenant isolation gate: even a misconfigured
    /// or partially-wired MCP surface must not leak data across user boundaries.
    /// </summary>
    [SkippableFact]
    public async Task CrossTenantQuery_UserA_CannotSeeUserBHolons()
    {
        Skip.IfNot(
            await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable at http://localhost:8442 — " +
            "start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        using var pFactory = new ParameterizedAuthFactory();

        var userAId = Guid.NewGuid();
        var userBId = Guid.NewGuid();

        // Seed user B's holons via the real API authenticated as B.
        using var clientB = pFactory.CreateClientForUser(userBId);

        // Create three holons "owned" by user B.  POST /api/holon uses
        // GetUserIdFromClaims() to stamp UserId on the created record.
        Guid targetHolonId = Guid.Empty;
        const string targetHolonName = "BHolon0";
        for (var i = 0; i < 2; i++)
        {
            var seedPayload = new
            {
                Name = $"BHolon{i}",
                Description = "Cross-tenant MCP scope fixture.",
                ProviderName = "test",
                UserId = userBId,
            };
            var seedResp = await clientB.PostAsJsonAsync("api/holon", seedPayload, _json);
            // If the seed endpoint returns an error here we still proceed —
            // the important assertion is that A cannot see B's data. If seeding
            // fails the holons simply don't exist, so A definitely can't see them.
            var seedBody = await seedResp.Content.ReadAsStringAsync();
            seedResp.IsSuccessStatusCode.Should().BeTrue(
                $"user B holon {i} must exist before cross-tenant access is tested; response: {seedBody}");

            using var seedDocument = JsonDocument.Parse(seedBody);
            if (!seedDocument.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("id", out var idElement)
                || !Guid.TryParse(idElement.GetString(), out var seededHolonId))
            {
                throw new InvalidOperationException(
                    $"User B holon {i} seed did not return a valid id: {seedBody}");
            }

            if (i == 0)
                targetHolonId = seededHolonId;
        }

        // Authenticate as user A and call the MCP tool surface, requesting
        // one of B's holon ids (or a fabricated one if seeding failed).
        using var clientA = pFactory.CreateClientForUser(userAId);
        targetHolonId.Should().NotBe(Guid.Empty,
            "the cross-tenant call must target a real user B holon");
        var mcpContent = McpToolCallPayload(
            "holon_traverse",
            new { holon_id = targetHolonId.ToString() });

        var mcpResp = await clientA.PostAsync("/mcp", mcpContent);

        // Core assertion: response must never contain B's holon data.
        // Acceptable outcomes: a 4xx or a JSON-RPC error body; never a 5xx.
        // Not acceptable: HTTP 200 with B's holon payload.
        var responseBody = await mcpResp.Content.ReadAsStringAsync();

        ((int)mcpResp.StatusCode).Should().BeLessThan(500,
            because: "a real cross-tenant MCP lookup must be rejected without a server fault");

        if (mcpResp.IsSuccessStatusCode)
        {
            // If the SDK returned 200, the body must be a JSON-RPC error or
            // "not found" — it must NEVER contain B's user id.
            responseBody.Should().NotContain(
                userBId.ToString(),
                because: "user A must never receive user B's data in an MCP response");
            responseBody.Should().NotContain(
                targetHolonId.ToString(),
                because: "user A must never receive user B's persisted holon id in an MCP response");
            responseBody.Should().NotContain(
                targetHolonName,
                because: "user A must never receive user B's holon payload in an MCP response");
        }
        else
        {
            // A client rejection proves the cross-tenant request was blocked.
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
    public async Task NoUserClaim_Returns401()
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
