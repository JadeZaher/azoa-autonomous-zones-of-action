using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Services.Auth;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

public sealed class MemberHandoffWebApplicationFactory : HttpFlowWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var settings = new Dictionary<string, string?>
        {
            ["Tenant:ClaimInvites:ReturnTokenToTenant"] = "false",
            ["Fiat:OffRamp:RequestVoidEnabled"] = "true",
            ["Tenant:ClaimInvites:Enabled"] = "true",
            ["Tenant:ClaimInvites:StubDeliveryAcknowledged"] = "true",
            ["Tenant:ClaimInvites:HostedBaseUrl"] = "https://node.example",
            ["Tenant:ClaimInvites:AllowedReturnOrigins:0"] = "https://app.example",
        };
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services =>
        {
            services.Configure<PolicySchemeOptions>("TestAuthForward", options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : context.Request.Headers.ContainsKey("X-Api-Key") ? ApiKeyAuthenticationHandler.SchemeName : TestAuthHandler.SchemeName);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ForwardAuthenticate = null;
                options.ForwardChallenge = null;
                options.ForwardDefaultSelector = context => context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? null : TestAuthHandler.SchemeName;
            });
        });
    }
}
