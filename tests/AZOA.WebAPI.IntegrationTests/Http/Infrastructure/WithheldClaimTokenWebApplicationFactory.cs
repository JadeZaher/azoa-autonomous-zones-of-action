using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>
/// The flow host with the production claim-invite policy restored: <c>appsettings.IntegrationTest.json</c>'s
/// token fixture is overridden off. See <c>Http/AGENTS.md</c> §claim-invite.
/// </summary>
public sealed class WithheldClaimTokenWebApplicationFactory : HttpFlowWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var settings = new Dictionary<string, string?> { ["Tenant:ClaimInvites:ReturnTokenToTenant"] = "false" };
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }
}
