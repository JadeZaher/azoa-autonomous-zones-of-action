using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>
/// The flow host with both webhook delivery workers polling every second and a one-attempt budget,
/// so a single receiver failure dead-letters. See <c>Http/AGENTS.md</c> §webhook-drain.
/// </summary>
public sealed class WebhookDrainWebApplicationFactory : HttpFlowWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var settings = new Dictionary<string, string?>
        {
            ["Webhooks:Enabled"] = "true",
            ["Webhooks:StartupDelaySeconds"] = "0",
            ["Webhooks:PollSeconds"] = "1",
            ["Webhooks:MaxAttempts"] = "1",
            ["Webhooks:HttpTimeoutSeconds"] = "5",
        };
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }
}
