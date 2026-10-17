using System.Net;
using System.Security.Cryptography;
using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Webhooks;
using AZOA.WebAPI.Services.Wormhole;
using AZOA.WebAPI.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>
/// The integration host with real DI, auth, routing and SurrealDB, configured as a node with
/// Identomat and Stripe enabled and both vendors answered in-process by <see cref="VendorEdge"/>.
/// Credentials are random per factory and live only in memory. See <c>Http/AGENTS.md</c> §factory.
/// </summary>
public class HttpFlowWebApplicationFactory : AZOATestWebApplicationFactory
{
    public const string AppOrigin = "https://app.flow.test";

    /// <summary>Public-range stand-in for every <c>*.flow.test</c> webhook receiver; never dialled.</summary>
    private static readonly IPAddress ReceiverAddress = IPAddress.Parse("198.51.99.10");

    public HttpFlowWebApplicationFactory()
    {
        CallbackApiKey = RandomSecret();
        Identomat = new IdentomatVendor(Edge, RandomSecret(), "kyc-config-" + Guid.NewGuid().ToString("N"),
            "kyb-config-" + Guid.NewGuid().ToString("N"));
        Stripe = new StripeVendor(Edge, RandomSecret(), RandomSecret());
    }

    /// <summary>The single outbound seam every vendor call goes through.</summary>
    public VendorEdge Edge { get; } = new();

    public IdentomatVendor Identomat { get; }

    public StripeVendor Stripe { get; }

    /// <summary>Shared secret Identomat carries in callback bodies (the node's only callback authentication).</summary>
    public string CallbackApiKey { get; }

    /// <summary>Warning-and-above server log lines, surfaced in flow assertion messages.</summary>
    public ServerLog ServerLog { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // UseSetting reaches the eager binds in Program.cs (FiatOptions is bound before Build);
        // the in-memory copy covers options read late. Same reasoning as the base factory's
        // SurrealDb settings.
        var settings = VendorSettings();
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureLogging(logging => logging.AddProvider(ServerLog));

        builder.ConfigureTestServices(services =>
        {
            // EVERY IHttpClientFactory client (named and typed: stripe, the Wormhole adapter,
            // anything a lane adds later) gets the edge as its primary handler. Only the test
            // SurrealDB is forwarded to the real network.
            Edge.AllowPassthrough(new Uri(SurrealTestDefaults.Endpoint));
            services.ConfigureHttpClientDefaults(client =>
                client.ConfigurePrimaryHttpMessageHandler(() => Edge.CreateHandler()));

            // Clients whose Program.cs registration sets its own primary handler: a name-specific
            // registration made after Program.cs wins regardless of how defaults are ordered.
            foreach (var name in new[]
                     {
                         IdentomatKycSettings.HttpClientName,
                         HostedIdentityHttp.ClientName,
                         WebhookOptions.HttpClientName,
                         WebhookOptions.ActionHttpClientName,
                     })
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Edge.CreateHandler());
            }
            services.AddHttpClient<WttGuardianTransport>().ConfigurePrimaryHttpMessageHandler(() => Edge.CreateHandler());

            // The webhook SSRF guard resolves *.flow.test to a public address instead of real DNS.
            var webhookGuard = new WebhookSsrfGuard();
            webhookGuard.UseResolver(host => host.EndsWith(".flow.test", StringComparison.OrdinalIgnoreCase)
                ? new[] { ReceiverAddress }
                : Dns.GetHostAddresses(host));
            services.AddSingleton(webhookGuard);
        });
    }

    private Dictionary<string, string?> VendorSettings() => new()
    {
        ["Kyc:Provider"] = "identomat",
        ["Kyc:Identomat:BeginBaseUrl"] = $"https://{IdentomatVendor.ApiHost}",
        ["Kyc:Identomat:ResultBaseUrl"] = $"https://{IdentomatVendor.WidgetHost}",
        ["Kyc:Identomat:CompanyKey"] = Identomat.CompanyKey,
        ["Kyc:Identomat:ConfigId"] = Identomat.KycConfigId,
        ["Kyc:Identomat:CallbackApiKey"] = CallbackApiKey,
        ["Kyc:Identity:PersonKeySecret"] = RandomSecret(),
        ["Kyb:ConfigId"] = Identomat.KybConfigId,
        ["Kyb:PolicyVersion"] = "flow-test-kyb-v1",
        // Lets tenant owners read their provider choices (the /kyc page); operator selection is unaffected.
        ["Kyc:TenantProviderOverride:Enabled"] = "true",
        ["Kyc:TenantProviderOverride:AllowedTenantIds:0"] = "*",

        ["Fiat:Enabled"] = "true",
        ["Fiat:Environment"] = "test",
        ["Fiat:ApiBaseUrl"] = $"https://{StripeVendor.ApiHost}",
        ["Fiat:Test:IsEnabled"] = "true",
        ["Fiat:Test:SecretKey"] = Stripe.SecretKey,
        ["Fiat:Test:WebhookSecret"] = Stripe.WebhookSecret,
        ["Fiat:Checkout:AllowedReturnOrigins:0"] = AppOrigin,
        ["Fiat:Checkout:DefaultSuccessUrl"] = $"{AppOrigin}/wallets",
        ["Fiat:Checkout:DefaultCancelUrl"] = $"{AppOrigin}/wallets",

        // Onboarding a tenant spends operator "financial" permits; keep the limiter in the
        // pipeline without letting a test class's own setup trip it.
        ["RateLimiting:Financial:PermitLimit"] = "500",
        ["RateLimiting:TenantCustodial:PermitLimit"] = "500",
    };

    /// <summary>48 hex characters: clears every 32-character secret floor the node enforces.</summary>
    private static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
