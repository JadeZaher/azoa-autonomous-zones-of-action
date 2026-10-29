using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace AZOA.WebAPI.IntegrationTests.Hosting;

/// <summary>
/// N1 security review: Tenant:ClaimInvites:ReturnTokenToTenant hands the raw claim token to the TENANT that
/// minted it, letting it claim a member's account it does not own. A single environment variable
/// (Tenant__ClaimInvites__ReturnTokenToTenant) sets it on any host, and appsettings.IntegrationTest.json
/// ships inside the production image, so this must be a boot-time refusal, not a config review item. See the
/// guard next to the claim-invite lifetime check in Program.cs and <see cref="ClaimInviteLifetimeStartupTests"/>
/// for the sibling guard it sits beside.
/// </summary>
public sealed class ClaimInviteReturnTokenToTenantStartupTests
{
    private const string RefusalMessage =
        "Tenant:ClaimInvites:ReturnTokenToTenant is refused outside Development/IntegrationTest.";

    /// <summary>The released service runs as Staging (never IsProduction()), so Staging must be refused too, not only Production.</summary>
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task A_staging_or_production_host_refuses_to_boot_with_the_token_returned(string environmentName)
    {
        var factory = new ReturnTokenEnabledFactory(environmentName);
        try
        {
            var start = () => factory.CreateClient();

            start.Should().Throw<Exception>()
                .Where(failure => failure.ToString().Contains(RefusalMessage, StringComparison.Ordinal));
        }
        finally
        {
            await factory.DisposeAsync();
            await SurrealTestSchema.DropAsync(factory.TestNamespace);
        }
    }

    /// <summary>The e2e compose stack runs as Development with this flag set; a developer's local run does too. Neither must regress.</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("IntegrationTest")]
    public async Task A_development_or_integration_test_host_boots_with_the_token_returned(string environmentName)
    {
        var factory = new ReturnTokenEnabledFactory(environmentName);
        try
        {
            var start = () => factory.CreateClient();

            start.Should().NotThrow();
        }
        finally
        {
            await factory.DisposeAsync();
            await SurrealTestSchema.DropAsync(factory.TestNamespace);
        }
    }

    /// <summary>
    /// Opts into ReturnTokenToTenant for the given environment. Outside Development/IntegrationTest, every
    /// OTHER fail-fast boot guard that runs earlier in Program.cs (secret guard, DataProtection, CORS, and —
    /// Production only — SurrealRuntimeConfigurationGuard) must also be satisfied, or the host would fail on
    /// one of those first and this test would not be exercising the guard it names.
    /// </summary>
    private sealed class ReturnTokenEnabledFactory : AZOATestWebApplicationFactory
    {
        private readonly string _environmentName;

        public ReturnTokenEnabledFactory(string environmentName) => _environmentName = environmentName;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment(_environmentName);
            builder.UseSetting("Tenant:ClaimInvites:ReturnTokenToTenant", "true");

            if (_environmentName is "Development" or "IntegrationTest")
                return;

            // Secret guard (Program.cs, just after DataProtection setup): outside Development/IntegrationTest
            // the JWT signing key and wallet encryption key must be real, non-placeholder values.
            builder.UseSetting("Jwt:Key", "staging-or-production-boot-guard-test-signing-key!!");
            builder.UseSetting("AZOA:WalletEncryptionKey", "staging-or-production-boot-guard-test-wallet-key!");
            // DataProtection:KeyRingPath is required outside Development/IntegrationTest so opaque cursors
            // survive a restart.
            builder.UseSetting(
                "DataProtection:KeyRingPath",
                Path.Combine(Path.GetTempPath(), "azoa-claim-invite-guard-test-" + Guid.NewGuid().ToString("N")));
            // Cors:AllowedOrigins must be non-empty outside Development/IntegrationTest.
            builder.UseSetting("Cors:AllowedOrigins:0", "https://example.azoa.test");

            if (_environmentName != "Production")
                return;

            // SurrealRuntimeConfigurationGuard.GuardProduction only runs when IsProduction(): the API must
            // bind a database-scoped runtime identity, never the legacy root one this factory's base
            // ConfigureWebHost sets for every other environment (and never the legacy SurrealDb section at
            // all, root or otherwise).
            builder.UseSetting("SurrealDb:User", string.Empty);
            builder.UseSetting("SurrealDb:Password", string.Empty);
            builder.UseSetting("SurrealRuntime:Endpoint", SurrealTestDefaults.Endpoint);
            builder.UseSetting("SurrealRuntime:Namespace", TestNamespace);
            builder.UseSetting("SurrealRuntime:Database", TestDatabase);
            builder.UseSetting("SurrealRuntime:User", "azoa_runtime");
            builder.UseSetting("SurrealRuntime:Password", "azoa_runtime_password");
            builder.UseSetting("SurrealRuntime:AuthenticationScope", SurrealRuntimeConfigurationGuard.DatabaseAuthenticationScope);
            builder.UseSetting("AZOA_SKIP_MIGRATIONS", "1");
        }
    }
}
