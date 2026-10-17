using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace AZOA.WebAPI.Tests.Consent;

/// <summary>D-5 flow: a tenant key issues a clamped child JWT that only allowlisted endpoints accept while consent stays live.</summary>
public sealed class DelegatedChildCredentialFlowTests
{
    private const string JwtKey = "super-secret-key-for-testing-only-quite-long-enough!";
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _child = Guid.NewGuid();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IConsentGrantStore> _grants = new();
    private bool _enabled = true;
    private bool _tenantActive = true;
    private DateTime? _grantRevokedAt;

    public DelegatedChildCredentialFlowTests()
    {
        _users.Setup(store => store.GetByIdAsync(_child, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<IUser>.Success(new User { Id = _child, IsActive = true }));
        _users.Setup(store => store.GetByIdAsync(_tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<IUser>.Success(new User { Id = _tenant, IsActive = _tenantActive }));
        _grants.Setup(store => store.ListByGrantorAsync(_child, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<IEnumerable<ConsentGrant>>.Success([new ConsentGrant
            {
                Id = Guid.NewGuid(), GrantorUserId = _child, TenantId = _tenant, GrantedAt = DateTime.UtcNow.AddMinutes(-5),
                RevokedAt = _grantRevokedAt,
                Scopes = [AzoaScopes.DappManage, AzoaScopes.ContractDeploySign, AzoaScopes.WalletManage, AzoaScopes.TransferSign],
            }]));
    }

    [Fact]
    public async Task Issued_child_credential_is_clamped_to_the_allowlist_and_admitted_by_an_allowlisted_endpoint()
    {
        var token = await IssueAsync([AzoaScopes.TenantProvision, AzoaScopes.DappManage, AzoaScopes.ContractDeploySign,
            AzoaScopes.WalletManage, AzoaScopes.TransferSign]);

        var child = Principal(token);
        child.GetScopes().Should().BeEquivalentTo([AzoaScopes.DappManage, AzoaScopes.ContractDeploySign],
            "wallet:manage and transfer:sign are granted but no allowlisted endpoint declares them, and tenant:provision is never delegated");
        child.GetActingTenantId().Should().Be(_tenant);
        (await Gate().RejectAsync(child, Endpoint(nameof(DeploymentsController.Start)))).Should().BeNull();
    }

    public static TheoryData<string> Rejections => new()
    {
        "node switch off", "endpoint not allowlisted", "issuing tenant deactivated", "user revoked the grant",
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task Child_credential_is_refused_per_request_once_any_admission_condition_lapses(string lapse)
    {
        var child = Principal(await IssueAsync([AzoaScopes.DappManage]));
        var action = nameof(DeploymentsController.Start);
        switch (lapse)
        {
            case "node switch off": _enabled = false; break;
            case "endpoint not allowlisted": action = nameof(DeploymentsController.Invoke); break;
            case "issuing tenant deactivated": _tenantActive = false; break;
            default: _grantRevokedAt = DateTime.UtcNow.AddSeconds(-1); break;
        }

        (await Gate().RejectAsync(child, Endpoint(action))).Should().NotBeNull();
    }

    private async Task<string> IssueAsync(string[] tenantKeyScopes)
    {
        var manager = new TenantManager(_users.Object, Configuration(), _grants.Object, ConsentFixture.Issuer());
        var controller = new TenantController(manager, Mock.Of<ITenantCustodialAccountManager>(), ConsentFixture.Factory(_tenant))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(tenantKeyScopes.Select(scope => new Claim("scope", scope))
                        .Append(new Claim(ClaimTypes.NameIdentifier, _tenant.ToString())).Append(new Claim("AuthMethod", "ApiKey")), "ApiKey")),
                },
            },
        };

        var issued = await controller.IssueChildCredential(_child, new IssueChildCredentialModel(), Configuration());

        var ok = issued.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((AZOAResult<ChildCredentialResponse>)ok.Value!).Result!.Token;
    }

    private DelegatedChildCredentialGate Gate() => new(Configuration(), _grants.Object, _users.Object);

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Key"] = JwtKey, ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test",
        [DelegatedChildCredentialGate.EnabledKey] = _enabled ? "true" : "false",
    }).Build();

    private static ClaimsPrincipal Principal(string token)
        => new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = "test", ValidAudience = "test", IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(JwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        }, out _);

    private static Endpoint Endpoint(string action)
        => new(null, new EndpointMetadataCollection(typeof(DeploymentsController).GetMethod(action)!.GetCustomAttributes(true)), action);
}
