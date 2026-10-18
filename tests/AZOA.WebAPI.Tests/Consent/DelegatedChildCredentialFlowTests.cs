using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Consent;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace AZOA.WebAPI.Tests.Consent;

/// <summary>
/// D-5 flow over HTTP: a tenant API key issues a child JWT through the real issue route, and the child calls
/// the real deployment routes through JWT validation, the <see cref="DelegatedChildCredentialGate.AdmitAsync"/>
/// token-validated hook and the production <c>DappManage</c> predicate (see Consent/AGENTS.md in this project).
/// </summary>
public sealed class DelegatedChildCredentialFlowTests : IAsyncLifetime
{
    private const string JwtKey = "super-secret-key-for-testing-only-quite-long-enough!";
    private const string RawTenantKey = "azoa_test_tenant_key_value";
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _child = Guid.NewGuid();
    private readonly Guid _tenantKeyId = Guid.NewGuid();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IConsentGrantStore> _grants = new();
    private readonly Mock<IApiKeyStore> _apiKeys = new();
    private readonly Mock<IDeploymentService> _deployments = new();
    private bool _enabled = true;
    private bool _tenantActive = true;
    private bool _childActive = true;
    private string _childRole = AzoaDappRoles.Manager;
    private DateTime? _grantRevokedAt;
    private DateTime? _tenantKeyRevokedAt;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public DelegatedChildCredentialFlowTests()
    {
        _users.Setup(store => store.GetByIdAsync(_child, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<IUser>.Success(new User { Id = _child, IsActive = _childActive, DappRole = _childRole }));
        _users.Setup(store => store.GetByIdAsync(_tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<IUser>.Success(new User { Id = _tenant, IsActive = _tenantActive }));
        _grants.Setup(store => store.ListByGrantorAsync(_child, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<IEnumerable<ConsentGrant>>.Success([new ConsentGrant
            {
                Id = Guid.NewGuid(), GrantorUserId = _child, TenantId = _tenant, GrantedAt = DateTime.UtcNow.AddMinutes(-5),
                RevokedAt = _grantRevokedAt,
                Scopes = [AzoaScopes.DappManage, AzoaScopes.ContractDeploySign, AzoaScopes.WalletManage, AzoaScopes.TransferSign],
            }]));
        _apiKeys.Setup(store => store.GetByHashAsync(ApiKeyAuthenticationHandler.HashKey(RawTenantKey), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TenantKey());
        _apiKeys.Setup(store => store.GetByIdForUserAsync(_tenantKeyId, _tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TenantKey());
        _deployments.Setup(service => service.GenerateAsync(It.IsAny<Guid>(), _child, _tenant, It.IsAny<DeploymentGenerateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<DeploymentArtifactView>.Success(new DeploymentArtifactView("artifact", "payload", "hash", Target, null)));
    }

    private static readonly DeploymentTarget Target = new("Runtime", "Algorand", "Devnet");

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(ConfigureServices)
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            })).StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Manager_child_credential_is_clamped_to_the_allowlist_and_reaches_an_allowlisted_dapp_manage_route()
    {
        var issued = await IssueAsync();

        issued.Scopes.Should().BeEquivalentTo([AzoaScopes.DappManage, AzoaScopes.ContractDeploySign],
            "wallet:manage and transfer:sign are granted but no allowlisted endpoint declares them, and tenant:provision is never delegated");
        (await GenerateAsync(issued.Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        _deployments.Verify(service => service.GenerateAsync(It.IsAny<Guid>(), _child, _tenant, It.IsAny<DeploymentGenerateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Tenant_cannot_delegate_dapp_manage_to_a_user_who_is_not_a_dapp_manager()
    {
        _childRole = AzoaDappRoles.User;

        var issued = await IssueAsync();

        issued.Scopes.Should().NotContain(AzoaScopes.DappManage,
            "the user's grant names dapp:manage, but a role-bound scope is delegable only while the user's live role holds it");
        (await GenerateAsync(issued.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _deployments.Invocations.Should().BeEmpty();
    }

    public static TheoryData<string> Lapses => new()
    {
        "user demoted from DApp manager", "node switch off", "issuing tenant deactivated", "user revoked the grant",
        "issuing tenant key revoked", "user deactivated",
    };

    [Theory]
    [MemberData(nameof(Lapses))]
    public async Task Child_credential_is_refused_per_request_once_any_admission_condition_lapses(string lapse)
    {
        var token = (await IssueAsync()).Token;
        switch (lapse)
        {
            case "user demoted from DApp manager": _childRole = AzoaDappRoles.User; break;
            case "node switch off": _enabled = false; break;
            case "issuing tenant deactivated": _tenantActive = false; break;
            case "user revoked the grant": _grantRevokedAt = DateTime.UtcNow.AddSeconds(-1); break;
            case "issuing tenant key revoked": _tenantKeyRevokedAt = DateTime.UtcNow.AddSeconds(-1); break;
            default: _childActive = false; break;
        }

        var generate = await GenerateAsync(token);
        var start = await PostAsync(token, "/api/deployments", new { });

        generate.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        start.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        _deployments.Invocations.Should().BeEmpty("a refused child credential must never reach the deployment service");
    }

    [Fact]
    public async Task Child_credential_is_refused_on_an_endpoint_that_is_not_allowlisted()
    {
        var token = (await IssueAsync()).Token;

        var response = await PostAsync(token, "/api/deployments/request/cancel", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dapp_manage_scope_on_a_delegated_token_without_a_live_manager_role_is_forbidden_by_the_policy()
    {
        // A child token on an endpoint whose admission scope is not role-bound still meets DappManage: the policy reads only
        // the store-loaded role identity the gate stamps, never the scope or a token-carried dapp_role claim.
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new(AzoaClaims.TokenUse, AzoaClaims.TokenUseChild), new("act_as_tenant", _tenant.ToString()),
            new("scope", AzoaScopes.DappManage), new("dapp_role", AzoaDappRoles.Manager),
        ], "Bearer"));

        principal.HasDappManageAccess().Should().BeFalse();
        principal.AddIdentity(new System.Security.Claims.ClaimsIdentity([new("dapp_role", AzoaDappRoles.Manager)], AzoaClaims.LiveRoleIdentity));
        principal.HasDappManageAccess().Should().BeTrue();
    }

    private ApiKey? TenantKey() => new()
    {
        Id = _tenantKeyId, UserId = _tenant, IsActive = true, RevokedAt = _tenantKeyRevokedAt,
        Scopes = string.Join(',', AzoaScopes.TenantProvision, AzoaScopes.DappManage, AzoaScopes.ContractDeploySign,
            AzoaScopes.WalletManage, AzoaScopes.TransferSign),
    };

    private HttpRequestMessage IssueRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tenant/users/{_child}/credential") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-Api-Key", RawTenantKey);
        return request;
    }

    private async Task<(string Token, IReadOnlyList<string> Scopes)> IssueAsync()
    {
        var response = await _client.SendAsync(IssueRequest());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = body.RootElement.GetProperty("result");
        return (result.GetProperty("token").GetString()!,
            result.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()!).ToArray());
    }

    private Task<HttpResponseMessage> GenerateAsync(string token)
        => PostAsync(token, $"/api/deployments/bundles/{Guid.NewGuid()}/generate", new { target = Target });

    private Task<HttpResponseMessage> PostAsync(string token, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return _client.SendAsync(request);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Scoped so each request sees the current node switch.
        services.AddScoped<IConfiguration>(_ => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = JwtKey, ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test",
            [DelegatedChildCredentialGate.EnabledKey] = _enabled ? "true" : "false",
        }).Build());
        services.AddScoped(_ => _users.Object);
        services.AddScoped(_ => _grants.Object);
        services.AddScoped(_ => _apiKeys.Object);
        services.AddScoped(_ => _deployments.Object);
        services.AddScoped<ITenantManager>(provider => new TenantManager(_users.Object, provider.GetRequiredService<IConfiguration>(),
            _grants.Object, ConsentFixture.Issuer()));
        services.AddScoped(_ => Mock.Of<ITenantCustodialAccountManager>());
        services.AddScoped<SigningAdmissionAuthorityFactory>(_ => ConsentFixture.Factory(_tenant));

        // Mirrors Program.cs: MultiScheme selector, JWT with the child-credential hook, and the API-key handler.
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "MultiScheme";
                options.DefaultChallengeScheme = "MultiScheme";
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                    ValidIssuer = "test", ValidAudience = "test", ClockSkew = TimeSpan.FromSeconds(30),
                    IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(JwtKey)),
                };
                options.Events = new JwtBearerEvents { OnTokenValidated = DelegatedChildCredentialGate.AdmitAsync };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { })
            .AddPolicyScheme("MultiScheme", "MultiScheme", options => options.ForwardDefaultSelector = AZOA.WebAPI.Services.Auth.AuthenticationSchemeSelector.Resolve);
        services.AddAuthorization(options =>
        {
            options.AddPolicy("TenantScope", policy => policy.RequireAssertion(ctx => ctx.User.HasScope(AzoaScopes.TenantProvision)));
            options.AddPolicy("DappManage", policy => policy.RequireAssertion(ctx => ctx.User.HasDappManageAccess()));
        });
        services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            manager.ApplicationParts.Clear();
            manager.ApplicationParts.Add(new AssemblyPart(typeof(DeploymentsController).Assembly));
            manager.FeatureProviders.Remove(manager.FeatureProviders.OfType<ControllerFeatureProvider>().Single());
            manager.FeatureProviders.Add(new OnlyControllers(typeof(DeploymentsController), typeof(TenantController)));
        });
    }

    private sealed class OnlyControllers(params Type[] controllers) : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo) => controllers.Contains(typeInfo.AsType());
    }
}
