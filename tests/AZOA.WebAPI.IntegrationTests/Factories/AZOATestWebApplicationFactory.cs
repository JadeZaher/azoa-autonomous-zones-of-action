using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
            // Replace the real auth pipeline with the test-only handler.
            // All requests that include the X-Test-Auth: true header are
            // automatically authenticated as the default test avatar.
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme    = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });

            // Wave 2 placeholder: when Worker B's ISurrealDbRepository is
            // registered in Program.cs, add any test-overrides here.
            // For now the EF-backed adapters from Program.cs remain wired.
        });
    }

    /// Create an HTTP client pre-configured with the test-auth header.
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        return client;
    }

    /// <summary>
    /// Create an HTTP client authenticated as a specific avatar id. Used by
    /// IDOR / multi-tenant integration tests that need to act as Avatar A in
    /// one request and Avatar B in the next.
    /// </summary>
    public HttpClient CreateAuthenticatedClientForAvatar(Guid avatarId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        client.DefaultRequestHeaders.Add(TestAuthHandler.AvatarHeaderName, avatarId.ToString());
        return client;
    }
}
