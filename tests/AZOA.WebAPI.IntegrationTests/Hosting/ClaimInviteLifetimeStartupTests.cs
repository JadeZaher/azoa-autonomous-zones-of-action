using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.IntegrationTests.Hosting;

/// <summary>An operator who sets an invite lifetime beyond the 7-day ceiling gets a failed boot naming the key, not a node that fails on the first invite.</summary>
public sealed class ClaimInviteLifetimeStartupTests
{
    [Fact]
    public async Task A_node_configured_with_an_invite_lifetime_over_seven_days_refuses_to_start()
    {
        var factory = new OverlongClaimInviteLifetimeFactory();
        try
        {
            var start = () => factory.CreateClient();

            start.Should().Throw<Exception>()
                .Where(failure => failure.ToString().Contains("Tenant:ClaimInvites:Lifetime", StringComparison.Ordinal));
        }
        finally
        {
            await factory.DisposeAsync();
            await SurrealTestSchema.DropAsync(factory.TestNamespace);
        }
    }

    private sealed class OverlongClaimInviteLifetimeFactory : AZOATestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Tenant:ClaimInvites:Lifetime"] = "8.00:00:00" }));
        }
    }
}
