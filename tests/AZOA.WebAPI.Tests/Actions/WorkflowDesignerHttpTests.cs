using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Designer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class WorkflowDesignerHttpTests
{
    [Fact]
    public async Task OnlyLiveTenantOwnerGetsContext_AndRevocationIsCheckedBeforeEveryEgress()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = Guid.NewGuid();
        using var client = fixture.Server.CreateClient();
        using var availability = await client.SendAsync(Request(HttpMethod.Get, owner));
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        var context = await availability.Content.ReadFromJsonAsync<AZOAResult<WorkflowDesignerAvailability>>();
        Assert.Equal(owner, context!.Result!.TenantId);
        fixture.TenantsActive = false;
        using var revoked = await client.SendAsync(Request(HttpMethod.Post, owner, owner));
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(0, fixture.Model.Calls);
    }

    [Fact]
    public async Task CrossOwnerSelectedContextAndNonTenantAreRejected_WithoutProviderCall()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = Guid.NewGuid();
        using var client = fixture.Server.CreateClient();
        using var crossOwner = await client.SendAsync(Request(HttpMethod.Post, owner, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, crossOwner.StatusCode);
        fixture.TenantsActive = false;
        using var unprovisioned = await client.SendAsync(Request(HttpMethod.Get, owner));
        Assert.Equal(HttpStatusCode.Forbidden, unprovisioned.StatusCode);
        Assert.Equal(0, fixture.Model.Calls);
    }

    [Fact]
    public async Task DeveloperScopeWithoutRole_AndDelegatedCredential_DoNotReachProvider()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = Guid.NewGuid();
        using var client = fixture.Server.CreateClient();
        using var noRole = Request(HttpMethod.Post, owner, owner, apiKey: Guid.NewGuid());
        noRole.Headers.Add("X-Test-Role", AzoaDappRoles.User);
        using var noRoleResult = await client.SendAsync(noRole);
        Assert.Equal(HttpStatusCode.Forbidden, noRoleResult.StatusCode);
        using var child = Request(HttpMethod.Post, owner, owner);
        child.Headers.Add("X-Test-Child", "true");
        using var childResult = await client.SendAsync(child);
        Assert.True(childResult.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        Assert.Equal(0, fixture.Model.Calls);
    }

    [Fact]
    public async Task RotatedKeysAndOwnerLoginShareTenantBudget_AnotherTenantHasOwnBudget()
    {
        await using var fixture = await Fixture.CreateAsync(permitLimit: 1);
        var owner = Guid.NewGuid();
        using var client = fixture.Server.CreateClient();
        using var first = await client.SendAsync(Request(HttpMethod.Post, owner, owner, Guid.NewGuid()));
        using var rotated = await client.SendAsync(Request(HttpMethod.Post, owner, owner, Guid.NewGuid()));
        using var login = await client.SendAsync(Request(HttpMethod.Post, owner, owner));
        var otherOwner = Guid.NewGuid();
        using var other = await client.SendAsync(Request(HttpMethod.Post, otherOwner, otherOwner, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rotated.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(2, fixture.Model.Calls);
    }

    [Fact]
    public async Task MissingConsentDoesNotCallProvider_AndAnonymousGetsChallenge()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = Guid.NewGuid();
        using var client = fixture.Server.CreateClient();
        using var noConsent = Request(HttpMethod.Post, owner, owner, consent: false);
        using var refused = await client.SendAsync(noConsent);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var anonymous = await client.GetAsync("/api/action-designer");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, fixture.Model.Calls);
    }

    private static HttpRequestMessage Request(HttpMethod method, Guid owner, Guid? tenant = null, Guid? apiKey = null, bool consent = true)
    {
        var request = new HttpRequestMessage(method, method == HttpMethod.Post ? "/api/action-designer/interview" : "/api/action-designer");
        request.Headers.Add("X-Test-Owner", owner.ToString());
        if (apiKey is not null) request.Headers.Add("X-Test-Key", apiKey.ToString());
        if (method == HttpMethod.Post) request.Content = JsonContent.Create(new WorkflowDesignerRequest {
            TenantId = tenant ?? Guid.Empty, ConsentToProvider = consent,
            Intake = new() { Purpose = "A cooperative asks members for a review." }
        });
        return request;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public bool TenantsActive { get; set; } = true;
        public FakeModel Model { get; } = new();
        private readonly IHost _host;
        public TestServer Server => _host.GetTestServer();
        private Fixture(int permitLimit)
        {
            var users = new Mock<IUserStore>();
            users.Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => AZOAResult<IUser>.Success(new User { Id = id, IsActive = true }));
            var keys = new Mock<IApiKeyStore>();
            keys.Setup(store => store.ListByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => (IReadOnlyList<ApiKey>)[new ApiKey { UserId = id, Scopes = AzoaScopes.TenantProvision, IsActive = TenantsActive }]);
            _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => {
                services.AddLogging();
                services.AddSingleton(users.Object);
                services.AddSingleton(keys.Object);
                services.AddSingleton<IWorkflowDesignerModel>(Model);
                services.AddSingleton<IActionDagValidator, ActionDagValidator>();
                services.AddSingleton<IActionDagExecutabilityValidator, ActionDagExecutabilityValidator>();
                services.AddScoped<WorkflowDesignerService>();
                services.AddAuthentication("designer-test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("designer-test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("DappDevelop", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasDappDevelopAccess())));
                services.AddRateLimiter(options => {
                    options.RejectionStatusCode = 429;
                    options.AddPolicy("workflow-designer", context => RateLimitPartition.GetFixedWindowLimiter(WorkflowDesignerPolicy.BudgetPartition(context.User), _ => new FixedWindowRateLimiterOptions {
                        PermitLimit = permitLimit, Window = TimeSpan.FromHours(1), QueueLimit = 0
                    }));
                });
                services.AddControllers().AddApplicationPart(typeof(ActionDesignerController).Assembly);
            }).Configure(app => {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            })).Build();
        }
        public static async Task<Fixture> CreateAsync(int permitLimit = 12)
        {
            var fixture = new Fixture(permitLimit);
            try
            {
                await fixture._host.StartAsync();
                return fixture;
            }
            catch
            {
                fixture._host.Dispose();
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            try { await _host.StopAsync(); }
            finally { _host.Dispose(); }
        }
    }

    private sealed class FakeModel : IWorkflowDesignerModel
    {
        public int Calls { get; private set; }
        public WorkflowDesignerAvailability Availability => new(true, "Test", "Test", "Test provider disclosure");
        public Task<WorkflowDesignerModelResult> InterviewAsync(WorkflowDesignerRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkflowDesignerModelResult(new() { Summary = "Which jurisdiction applies?", Questions = ["Where does the tenant operate?"] }));
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Owner", out var owner)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, owner.ToString()), new("sub", owner.ToString()),
                new("dapp_role", Request.Headers["X-Test-Role"].FirstOrDefault() ?? AzoaDappRoles.Developer), new("scope", AzoaScopes.DappDevelop),
                new(AzoaClaims.TokenUse, Request.Headers.ContainsKey("X-Test-Child") ? AzoaClaims.TokenUseChild : AzoaClaims.TokenUseLogin) };
            if (Request.Headers.TryGetValue("X-Test-Key", out var key)) { claims.Add(new("AuthMethod", "ApiKey")); claims.Add(new("ApiKeyId", key.ToString())); }
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
