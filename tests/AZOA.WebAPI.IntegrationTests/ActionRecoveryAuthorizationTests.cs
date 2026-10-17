using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.IntegrationTests;

/// <summary>Exercises production recovery authorization with controlled authenticated principals.</summary>
public sealed class ActionRecoveryAuthorizationTests
{
    [Fact]
    public async Task Production_recovery_pipeline_requires_recent_reserved_operator_without_tenant_context()
    {
        var factory = new RecoveryAuthorizationFactory();
        try
        {
            using var client = factory.CreateClient();
            var id = Guid.NewGuid();
            var route = $"/api/operator/action-recovery/{id}";
            var request = new ActionRecoveryRequest(Guid.NewGuid(), 0, new string('a', 64),
                "Reviewed the original operation and its saved authority.");
            foreach (var scenario in new[] { "anonymous", "login", "wrong-subject", "stale", "future", "missing-time", "malformed-time", "tenant" })
            {
                var expected = scenario == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
                using var get = new HttpRequestMessage(HttpMethod.Get, route);
                get.Headers.Add(RecoveryPrincipalHandler.Header, scenario);
                using var read = await client.SendAsync(get);
                read.StatusCode.Should().Be(expected, scenario);
                using var post = new HttpRequestMessage(HttpMethod.Post, route + "/retry") { Content = JsonContent.Create(request) };
                post.Headers.Add(RecoveryPrincipalHandler.Header, scenario);
                using var mutation = await client.SendAsync(post);
                mutation.StatusCode.Should().Be(expected, scenario);
            }

            using var admittedGet = new HttpRequestMessage(HttpMethod.Get, route);
            admittedGet.Headers.Add(RecoveryPrincipalHandler.Header, "recent");
            using var admittedRead = await client.SendAsync(admittedGet);
            admittedRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
            admittedRead.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await admittedRead.Content.ReadAsStringAsync()).Should().Contain("Action execution was not found.");

            using var admittedPost = new HttpRequestMessage(HttpMethod.Post, route + "/retry") { Content = JsonContent.Create(request) };
            admittedPost.Headers.Add(RecoveryPrincipalHandler.Header, "recent");
            using var admittedMutation = await client.SendAsync(admittedPost);
            admittedMutation.StatusCode.Should().Be(HttpStatusCode.Conflict);
            admittedMutation.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await admittedMutation.Content.ReadAsStringAsync()).Should().Contain("unchanged recoverable parked operation");
        }
        finally
        {
            await factory.DisposeAsync();
            await SurrealTestSchema.DropAsync(factory.TestNamespace);
        }
    }

    private sealed class RecoveryAuthorizationFactory : AZOATestWebApplicationFactory
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            SurrealTestSchema.BootstrapAsync(TestNamespace, "action_node_execution", "action_recovery_audit")
                .GetAwaiter().GetResult();
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = RecoveryPrincipalHandler.SchemeName;
                    options.DefaultAuthenticateScheme = RecoveryPrincipalHandler.SchemeName;
                    options.DefaultChallengeScheme = RecoveryPrincipalHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, RecoveryPrincipalHandler>(RecoveryPrincipalHandler.SchemeName, _ => { });
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.ForwardAuthenticate = RecoveryPrincipalHandler.SchemeName;
                    options.ForwardChallenge = RecoveryPrincipalHandler.SchemeName;
                    options.ForwardForbid = RecoveryPrincipalHandler.SchemeName;
                });
            });
        }
    }

    private sealed class RecoveryPrincipalHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "RecoveryPrincipal";
        public const string Header = "X-Recovery-Test-Principal";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var scenario = Request.Headers[Header].ToString();
            if (scenario is "" or "anonymous") return Task.FromResult(AuthenticateResult.NoResult());
            var subject = scenario == "wrong-subject" ? Guid.NewGuid() : NodeOperatorIdentity.UserId;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, subject.ToString()),
                new("sub", subject.ToString()),
                new(AzoaClaims.TokenUse, scenario == "login" ? AzoaClaims.TokenUseLogin : AzoaClaims.TokenUseNodeOperator),
                new("scope", AzoaScopes.Operator),
                new(AzoaClaims.OperatorRevision, "1")
            };
            if (scenario != "missing-time")
            {
                var time = DateTimeOffset.UtcNow.AddMinutes(scenario == "stale" ? -11 : scenario == "future" ? 2 : 0);
                claims.Add(new(AzoaClaims.AuthTime, scenario == "malformed-time" ? "invalid" : time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
            }
            if (scenario == "tenant") claims.Add(new("act_as_tenant", Guid.NewGuid().ToString()));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
