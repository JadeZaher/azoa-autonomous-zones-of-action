using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Auth;

namespace AZOA.WebAPI.IntegrationTests.Factories;

/// <summary>
/// Test host for integration tests against a real SurrealDB container.
///
/// Design:
///   - NO EF InMemory swap. NO AZOADbContext.
///   - NO db.Database.Migrate() (that was a relational-only boot path, removed here).
///   - Authentication is replaced by the test-only TestAuthHandler so tests
///     can exercise auth-gated endpoints without real JWT tokens.
///   - The SurrealDB connection defaults to
///     the developer's local SurrealDB instance on 127.0.0.1:8000 (see appsettings.Development.json).
///   - Per-test namespace isolation is owned by IntegrationTestBase — the
///     factory itself is shared across the test class collection (IClassFixture).
///
/// Storage backend wiring:
///   When the SurrealDB adapter (Worker B: ISurrealDbRepository) exists it will
///   be registered in Program.cs via the IStorageProvider seam. Until then the
///   existing EF-backed adapters remain wired from Program.cs — that is fine
///   because the factory does NOT swap the storage, only the auth scheme.
///   The adapter swap is wave 2 (tasks 5–8 of the migration plan).
/// </summary>
public class AZOATestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// SurrealDB namespace the in-process app writes to for this factory.
    ///
    /// One factory exists per test class (IClassFixture), so this value gives
    /// per-class isolation: classes run in parallel under their own namespace,
    /// methods within a class share it and run serially. The value is a
    /// server-safe Guid-hex identifier (no hyphens) generated once per factory.
    ///
    /// IntegrationTestBase reads this so the namespace it CREATES + applies the
    /// generated schema to is the SAME namespace the app CONNECTS to. Before
    /// this was wired the factory left SurrealDb:Namespace at its "azoa" default
    /// while the harness created a different per-test guid namespace — every
    /// controller write then faulted with "The namespace 'azoa' does not exist".
    /// </summary>
    public string TestNamespace { get; } = $"itest{Guid.NewGuid():N}";

    /// <summary>Database the app + harness agree on (created by IntegrationTestBase).</summary>
    public const string TestDatabase = "test";

    /// <summary>Forwarding policy scheme (test-only) that routes X-Api-Key requests to
    /// the real ApiKey handler and everything else to TestAuthHandler.</summary>
    private const string TestAuthForwardScheme = "TestAuthForward";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTest");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Minimal JWT config so the JWT middleware initialises without
            // throwing; the TestAuthHandler takes over auth in the test pipeline.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"]      = "super-secret-test-key-that-is-long-enough!",
                ["Jwt:Issuer"]   = "test",
                ["Jwt:Audience"] = "test",

                // SurrealDB connection for the test host (wave 2: adapter wiring).
                // The options class properties are Endpoint / User / Password -- the
                // ":Username" key on the previous baseline did NOT bind, so requests
                // hit Surreal anonymous and got rejected with -32002 permission errors.
                ["SurrealDb:Endpoint"] = SurrealTestDefaults.Endpoint,
                ["SurrealDb:User"]     = SurrealTestDefaults.User,
                ["SurrealDb:Password"] = SurrealTestDefaults.Password,

                // Pin the app's namespace/database to the per-class test scope so
                // the app writes to the SAME namespace IntegrationTestBase creates
                // and schemas. Without these two keys the app fell back to the
                // "azoa" default (SurrealConnectionOptions) which no test ever
                // creates -> "The namespace 'azoa' does not exist" on every write.
                ["SurrealDb:Namespace"] = TestNamespace,
                ["SurrealDb:Database"]  = TestDatabase,

                // Keep the AZOA provider key so Program.cs provider-selection code
                // (if any) doesn't throw on missing config.
                ["AZOA:DefaultProvider"] = "SurrealDb"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Add the test-only handler alongside the REAL schemes registered in
            // Program.cs (JWT, ApiKey, MultiScheme). Re-calling AddAuthentication
            // reconfigures the options delegate without clearing the existing
            // SchemeMap, so ApiKeyAuthenticationHandler survives and can be exercised.
            //
            // avatar-dapp-rbac AC3 (keystone): the default scheme is a FORWARDING
            // policy scheme so an X-Api-Key request routes to the REAL
            // ApiKeyAuthenticationHandler (which re-reads the owner's CURRENT DappRole),
            // while every other request routes to TestAuthHandler (the JWT-equivalent
            // fake). This is what lets the stale-scope revocation test hit real auth
            // enforcement instead of the always-manager test principal.
            services.AddAuthentication(options =>
            {
                options.DefaultScheme             = TestAuthForwardScheme;
                options.DefaultAuthenticateScheme = TestAuthForwardScheme;
                options.DefaultChallengeScheme    = TestAuthForwardScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { })
            .AddPolicyScheme(TestAuthForwardScheme, TestAuthForwardScheme, options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey("X-Api-Key")
                        ? ApiKeyAuthenticationHandler.SchemeName
                        : TestAuthHandler.SchemeName;
            });

            // Wave 2 placeholder: when Worker B's ISurrealDbRepository is
            // registered in Program.cs, add any test-overrides here.
            // For now the EF-backed adapters from Program.cs remain wired.
        });
    }

    /// Create an HTTP client pre-configured with the test-auth header.
    public HttpClient CreateAuthenticatedClient(string? dappRole = AzoaDappRoles.Manager)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        if (!string.IsNullOrWhiteSpace(dappRole))
            client.DefaultRequestHeaders.Add(TestAuthHandler.DappRoleHeaderName, dappRole);
        return client;
    }

    /// <summary>
    /// Create an HTTP client authenticated as a specific avatar id. Used by
    /// IDOR / multi-tenant integration tests that need to act as Avatar A in
    /// one request and Avatar B in the next.
    /// </summary>
    public HttpClient CreateAuthenticatedClientForAvatar(Guid avatarId, string? dappRole = AzoaDappRoles.Manager)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        client.DefaultRequestHeaders.Add(TestAuthHandler.AvatarHeaderName, avatarId.ToString());
        if (!string.IsNullOrWhiteSpace(dappRole))
            client.DefaultRequestHeaders.Add(TestAuthHandler.DappRoleHeaderName, dappRole);
        return client;
    }

    /// <summary>
    /// avatar-dapp-rbac: an operator-authenticated client (JWT-equivalent scheme with
    /// operator:admin + role=Admin stamped). Used to exercise operator-gated surfaces
    /// and the role-assignment bootstrap path. Optionally pins an avatar id.
    /// </summary>
    public HttpClient CreateOperatorClient(Guid? avatarId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        client.DefaultRequestHeaders.Add(TestAuthHandler.OperatorHeaderName, "true");
        if (avatarId.HasValue)
            client.DefaultRequestHeaders.Add(TestAuthHandler.AvatarHeaderName, avatarId.Value.ToString());
        return client;
    }

    public HttpClient CreateOperatorOnlyClient(Guid? avatarId = null)
    {
        var client = CreateOperatorClient(avatarId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.OperatorOnlyHeaderName, "true");
        return client;
    }

    public HttpClient CreateNodeGovernClient(Guid? avatarId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        client.DefaultRequestHeaders.Add(TestAuthHandler.NodeGovernHeaderName, "true");
        if (avatarId.HasValue)
            client.DefaultRequestHeaders.Add(TestAuthHandler.AvatarHeaderName, avatarId.Value.ToString());
        return client;
    }

    /// <summary>
    /// avatar-dapp-rbac AC3 (keystone): an HTTP client that authenticates via the REAL
    /// ApiKeyAuthenticationHandler using a genuinely-minted raw key (X-Api-Key header, no
    /// X-Test-Auth). The forwarding policy scheme routes it to the real handler, which
    /// re-reads the owner avatar's CURRENT DappRole — so a key minted while the owner was
    /// a developer is correctly denied after the owner is demoted.
    /// </summary>
    public HttpClient CreateApiKeyClient(string rawKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", rawKey);
        return client;
    }
}
