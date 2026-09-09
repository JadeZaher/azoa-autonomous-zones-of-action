// SPDX-License-Identifier: UNLICENSED
// W1.4 node identity policy. The three gates below are one subject - who decides
// identity on this node - so they are proved together: the operator chooses the
// KYC provider, the operator chooses who may create an account, and the vendor
// only ever receives a return URL the operator vetted.

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class NodeIdentityPolicyGateTests
{
    private static readonly Guid TenantId = Guid.Parse("7f9c1d2e-3a4b-4c5d-8e9f-0a1b2c3d4e5f");

    // ── Tenant KYC provider override (D48) ──────────────────────────────────

    [Fact]
    public void TenantOverride_WithNoConfiguration_IsOff()
        => TenantKycProviderOverridePolicy
            .IsAllowedForTenant(new ConfigurationBuilder().Build(), TenantId)
            .Should().BeFalse();

    [Fact]
    public void TenantOverride_EnabledWithAnEmptyAllowList_IsStillOff()
    {
        var configuration = OverrideConfiguration(enabled: true);

        TenantKycProviderOverridePolicy.IsAllowedForTenant(configuration, TenantId)
            .Should().BeFalse("the node switch alone never admits a tenant");
    }

    [Fact]
    public void TenantOverride_AllowListedWithoutTheNodeSwitch_IsStillOff()
    {
        var configuration = OverrideConfiguration(enabled: false, TenantId.ToString());

        TenantKycProviderOverridePolicy.IsAllowedForTenant(configuration, TenantId)
            .Should().BeFalse("both keys must agree");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void TenantOverride_UnparseableSwitch_ReadsAsOff(string switchValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TenantKycProviderOverridePolicy.EnabledKey] = switchValue,
                [$"{TenantKycProviderOverridePolicy.AllowedTenantIdsKey}:0"] = TenantId.ToString(),
            })
            .Build();

        TenantKycProviderOverridePolicy.IsAllowedForTenant(configuration, TenantId).Should().BeFalse();
    }

    [Fact]
    public void TenantOverride_AllowListedTenant_IsAdmitted()
    {
        var configuration = OverrideConfiguration(
            enabled: true,
            Guid.NewGuid().ToString(),
            TenantId.ToString("N"));

        TenantKycProviderOverridePolicy.IsAllowedForTenant(configuration, TenantId).Should().BeTrue();
        TenantKycProviderOverridePolicy.IsAllowedForTenant(configuration, Guid.NewGuid())
            .Should().BeFalse("a different tenant is not on the list");
    }

    [Fact]
    public void TenantOverride_WildcardEntry_AdmitsEveryTenant()
    {
        var configuration = OverrideConfiguration(
            enabled: true,
            TenantKycProviderOverridePolicy.AllowAllTenantsEntry);

        TenantKycProviderOverridePolicy.IsAllowedForTenant(configuration, Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public async Task SelectTenantProvider_ByTheTenant_IsRefusedUntilTheOperatorAdmitsIt()
    {
        var registry = new Mock<IKycProviderRegistry>(MockBehavior.Strict);
        var manager = ControlPlane(OverrideConfiguration(enabled: false), registry);

        var result = await manager.SelectTenantProviderAsync(
            TenantId,
            new SelectTenantKycProviderRequest { ProviderKey = "identomat", ExpectedVersion = 0 },
            TenantId,
            requireTenantAuthority: true);

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.Forbidden);
        result.Message.Should().Contain(TenantKycProviderOverridePolicy.ForbiddenReason);
        // Strict mock: the refusal happens before the registry is consulted at all.
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SelectTenantProvider_ByTheOperator_IsNeverGatedByTheTenantAllowList()
    {
        var registry = new Mock<IKycProviderRegistry>();
        registry.Setup(item => item.EvaluateProfileAsync("identomat", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycProviderProfileResponse>.Success(new KycProviderProfileResponse
            {
                ProviderKey = "identomat",
                Enabled = false,
                Available = false,
                ReadinessCode = KycProviderReadinessCodes.SecretsNotConfigured,
            }));
        var manager = ControlPlane(OverrideConfiguration(enabled: false), registry);

        var result = await manager.SelectTenantProviderAsync(
            TenantId,
            new SelectTenantKycProviderRequest { ProviderKey = "identomat", ExpectedVersion = 0 },
            Guid.NewGuid(),
            requireTenantAuthority: false);

        result.IsError.Should().BeTrue();
        result.Message.Should().NotContain(
            TenantKycProviderOverridePolicy.ForbiddenReason,
            "the operator is the custodian; only the provider readiness may stop them");
        result.Message.Should().Contain(KycProviderReadinessCodes.SecretsNotConfigured);
    }

    [Fact]
    public async Task ListTenantChoices_WithoutTheOverride_ShowsAnEmptyMenu()
    {
        var registry = new Mock<IKycProviderRegistry>(MockBehavior.Strict);
        var manager = ControlPlane(OverrideConfiguration(enabled: false), registry);

        var result = await manager.ListTenantChoicesAsync(TenantId);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().BeEmpty("a tenant is never shown a menu it cannot act on");
        registry.VerifyNoOtherCalls();
    }

    // ── Public self-registration (gotcha 22) ────────────────────────────────

    [Theory]
    [InlineData("Development", null, null, true)]
    [InlineData("Development", "false", null, false)]
    [InlineData("IntegrationTest", null, null, true)]
    [InlineData("Production", null, null, false)]
    [InlineData("Production", "true", null, false)]
    [InlineData("Production", "true", "false", false)]
    [InlineData("Production", "true", "true", true)]
    [InlineData("Production", "false", "true", false)]
    [InlineData("Production", "yes", "true", false)]
    public void SelfRegistration_ResolvesFailClosedInProduction(
        string environmentName,
        string? enabled,
        string? acknowledged,
        bool expected)
    {
        var values = new Dictionary<string, string?>();
        if (enabled is not null)
            values[SelfRegistrationPolicy.EnabledKey] = enabled;
        if (acknowledged is not null)
            values[SelfRegistrationPolicy.ProductionAcknowledgementKey] = acknowledged;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        SelfRegistrationPolicy.IsAllowed(configuration, EnvironmentNamed(environmentName))
            .Should().Be(expected);
    }

    [Fact]
    public async Task Register_OnAProductionNode_IsForbiddenAndNeverReachesTheManager()
    {
        var manager = new Mock<IUserManager>(MockBehavior.Strict);
        var controller = new UserController(
            manager.Object,
            new ConfigurationBuilder().Build(),
            EnvironmentNamed(Environments.Production));

        var response = await controller.Register(
            new UserRegisterModel { Username = "stranger", Email = "stranger@example.com", Password = "Secret123" },
            null);

        var status = response.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        status.Value.Should().BeOfType<AZOAResult<IUser>>()
            .Which.Message.Should().Be(SelfRegistrationPolicy.DisabledMessage);
        manager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegisterAsync_OnAProductionNode_WritesNothing()
    {
        var store = new Mock<IUserStore>(MockBehavior.Strict);
        var manager = new UserManager(
            store.Object,
            new ConfigurationBuilder().Build(),
            adminBootstrapStateStore: null,
            environment: EnvironmentNamed(Environments.Production));

        var result = await manager.RegisterAsync(new UserRegisterModel
        {
            Username = "stranger",
            Email = "stranger@example.com",
            Password = "Secret123",
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(SelfRegistrationPolicy.DisabledMessage);
        // The manager seam refuses on its own, so a non-HTTP caller inherits it.
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegisterAsync_LeavesUniquenessToTheIndexInsteadOfScanningEveryUser()
    {
        var store = new Mock<IUserStore>();
        store.Setup(item => item.UpsertAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IUser user, CancellationToken _) => new AZOAResult<IUser> { Result = user });
        var manager = new UserManager(store.Object, new ConfigurationBuilder().Build());

        var result = await manager.RegisterAsync(new UserRegisterModel
        {
            Username = "  Neo  ",
            Email = " Neo@Matrix.COM ",
            Password = "Secret123",
        });

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Username.Should().Be("neo");
        result.Result.Email.Should().Be("neo@matrix.com");
        result.Result.PasswordHash.Should().NotBe("Secret123");
        store.Verify(
            item => item.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "the UNIQUE indexes adjudicate uniqueness; the O(n) scan was both slow and racy");
    }

    [Fact]
    public async Task RegisterAsync_WhenTheWriteIsRefused_SaysSoWithoutReadingTheUserTable()
    {
        var store = new Mock<IUserStore>(MockBehavior.Strict);
        store.Setup(item => item.UpsertAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.FailureWithCode(
                "USER_STORE_UNAVAILABLE: Identity persistence is temporarily unavailable.",
                AzoaErrorCodes.DependencyUnavailable));
        var manager = new UserManager(store.Object, new ConfigurationBuilder().Build());

        var result = await manager.RegisterAsync(new UserRegisterModel
        {
            Username = "neo",
            Email = "neo@matrix.com",
            Password = "Secret123",
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(UserManager.RegistrationRefusedMessage);
        // Strict mock: any read at all on the anonymous failure path throws. Naming
        // the field that collided used to cost a full scan a stranger could force,
        // and told that stranger whether the address is registered.
        store.Verify(
            item => item.UpsertAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()),
            Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateAsync_PersistsTheSameFoldedFormRegistrationPersists()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "neo",
            Email = "neo@matrix.com",
            PasswordHash = "hash",
        };
        var store = new Mock<IUserStore>();
        store.Setup(item => item.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = user });
        store.Setup(item => item.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = new IUser[] { user } });
        store.Setup(item => item.UpsertAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IUser saved, CancellationToken _) => new AZOAResult<IUser> { Result = saved });
        var manager = new UserManager(store.Object, new ConfigurationBuilder().Build());

        var result = await manager.UpdateAsync(
            user.Id,
            new UserUpdateModel { Email = "  Neo@Matrix.COM  ", Username = "  NEO  " },
            user.Id);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Email.Should().Be(
            "neo@matrix.com",
            "user_email is case-sensitive, so a re-cased profile update would otherwise "
            + "walk straight past the UNIQUE index and leave two rows for one address");
        result.Result.Username.Should().Be("neo");
    }

    [Theory]
    [InlineData("neo@matrix.com", "lower-hash")]
    [InlineData("Neo@Matrix.com", "upper-hash")]
    public async Task LoginAsync_WithTwoRowsFoldingToOneAddress_ResolvesTheExactRow(
        string typedAddress,
        string password)
    {
        var manager = ManagerOverUsers(
            new User { Id = Guid.NewGuid(), Email = "neo@matrix.com", PasswordHash = Hash("lower-hash") },
            new User { Id = Guid.NewGuid(), Email = "Neo@Matrix.com", PasswordHash = Hash("upper-hash") });

        var result = await manager.LoginAsync(new UserLoginModel { Email = typedAddress, Password = password });

        result.IsError.Should().BeFalse(result.Message);
    }

    [Fact]
    public async Task LoginAsync_WithTwoRowsFoldingToOneAddressAndNoExactRow_ResolvesNothing()
    {
        var manager = ManagerOverUsers(
            new User { Id = Guid.NewGuid(), Email = "neo@matrix.com", PasswordHash = Hash("lower-hash") },
            new User { Id = Guid.NewGuid(), Email = "Neo@Matrix.com", PasswordHash = Hash("upper-hash") });

        var result = await manager.LoginAsync(new UserLoginModel
        {
            Email = "NEO@MATRIX.COM",
            Password = "lower-hash",
        });

        result.IsError.Should().BeTrue(
            "row order must never decide which of two password hashes an address authenticates against");
        result.Message.Should().Be("Invalid credentials.");
    }

    [Fact]
    public async Task LoginAsync_WithOneRowWrittenBeforeFolding_StillResolvesCaseInsensitively()
    {
        var manager = ManagerOverUsers(
            new User { Id = Guid.NewGuid(), Email = "Neo@Matrix.com", PasswordHash = Hash("upper-hash") });

        var result = await manager.LoginAsync(new UserLoginModel
        {
            Email = "neo@matrix.com",
            Password = "upper-hash",
        });

        result.IsError.Should().BeFalse(result.Message);
    }

    // ── Identomat return URL ────────────────────────────────────────────────

    [Fact]
    public async Task BeginSession_ForwardsTheConfiguredReturnUrlAsTheDocumentedGeneralOverride()
    {
        var captured = CaptureBeginBody(out var transport);
        var provider = IdentomatWithReturnUrl(transport, "https://app.example.com/identity/done");

        var created = await provider.CreateHostedSessionAsync(BeginRequest());

        created.IsError.Should().BeFalse(created.Message);
        captured().Should().NotBeNull();
        captured()!.General.Should().NotBeNull();
        captured()!.General!.ReturnUrl.Should().Be("https://app.example.com/identity/done");
    }

    [Fact]
    public async Task BeginSession_WithNoConfiguredReturnUrl_SendsNoOverrideObjectAtAll()
    {
        var captured = CaptureBeginBody(out var transport);
        var provider = IdentomatWithReturnUrl(transport, null);

        var created = await provider.CreateHostedSessionAsync(BeginRequest());

        created.IsError.Should().BeFalse(created.Message);
        captured()!.General.Should().BeNull("an absent key must leave the vendor configuration alone");
    }

    [Theory]
    [InlineData("http://app.example.com/done")]
    [InlineData("https://localhost:3000/done")]
    [InlineData("https://192.168.1.10/done")]
    [InlineData("https://user:pass@app.example.com/done")]
    [InlineData("not-a-url")]
    public async Task BeginSession_DropsAReturnUrlThatIsNotAPublicHttpsTarget(string configured)
    {
        var captured = CaptureBeginBody(out var transport);
        var provider = IdentomatWithReturnUrl(transport, configured);

        var created = await provider.CreateHostedSessionAsync(BeginRequest());

        created.IsError.Should().BeFalse(created.Message);
        captured()!.General.Should().BeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IConfiguration OverrideConfiguration(bool enabled, params string[] allowedTenantIds)
    {
        var values = new Dictionary<string, string?>
        {
            [TenantKycProviderOverridePolicy.EnabledKey] = enabled ? "true" : "false",
        };
        for (var index = 0; index < allowedTenantIds.Length; index++)
            values[$"{TenantKycProviderOverridePolicy.AllowedTenantIdsKey}:{index}"] = allowedTenantIds[index];
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static KycControlPlaneManager ControlPlane(
        IConfiguration configuration,
        Mock<IKycProviderRegistry> registry)
    {
        var users = new Mock<IUserStore>();
        users.Setup(item => item.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser>
            {
                Result = new User { Id = TenantId, Username = "tenant", IsActive = true },
            });
        var apiKeys = new Mock<IApiKeyStore>();
        apiKeys.Setup(item => item.ListByUserAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AZOA.WebAPI.Models.ApiKey>
            {
                new() { UserId = TenantId, IsActive = true, Scopes = AzoaScopes.TenantProvision },
            });

        return new KycControlPlaneManager(
            Mock.Of<IKycControlStore>(),
            Mock.Of<IKycStore>(),
            registry.Object,
            Mock.Of<IKycManager>(),
            apiKeys.Object,
            users.Object,
            Mock.Of<IAdminBootstrapStateStore>(),
            EnvironmentNamed(Environments.Production),
            configuration);
    }

    private static IHostEnvironment EnvironmentNamed(string environmentName)
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == environmentName);

    private static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    /// <summary>A manager whose only user rows are the ones given, with a signing key.</summary>
    private static UserManager ManagerOverUsers(params IUser[] users)
    {
        var store = new Mock<IUserStore>();
        store.Setup(item => item.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = users });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "node-identity-policy-gate-signing-key-for-tests!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test",
            })
            .Build();

        return new UserManager(store.Object, configuration);
    }

    private static HostedSessionRequest BeginRequest() => new()
    {
        FlowKind = HostedSessionFlowKinds.Kyc,
        SubjectUserId = Guid.NewGuid(),
        AdmissionKey = "admission-key",
    };

    /// <summary>Wires a transport that records the exact /begin body the adapter sends.</summary>
    private static Func<IdentomatBeginRequestWithSessionOverrides?> CaptureBeginBody(
        out IIdentomatHttpTransport transport)
    {
        IdentomatBeginRequestWithSessionOverrides? body = null;
        var mock = new Mock<IIdentomatHttpTransport>();
        mock.Setup(item => item.PostAsync<IdentomatBeginRequestWithSessionOverrides, IdentomatBeginResponse>(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IdentomatBeginRequestWithSessionOverrides>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, string __, IdentomatBeginRequestWithSessionOverrides sent, string ___, CancellationToken ____) => body = sent)
            .ReturnsAsync(AZOAResult<IdentomatBeginResponse>.Success(new IdentomatBeginResponse
            {
                Id = "parent-session-id",
                AdditionalSessionId = "child-session-id",
                Url = "https://widget.identomat.com/?session_token=child-session-id",
            }));
        transport = mock.Object;
        return () => body;
    }

    private static IdentomatKycProviderService IdentomatWithReturnUrl(
        IIdentomatHttpTransport transport,
        string? returnUrl)
    {
        var values = new Dictionary<string, string?>();
        if (returnUrl is not null)
            values[IdentomatKycProviderService.ReturnUrlConfigurationKey] = returnUrl;

        return new IdentomatKycProviderService(
            IdentomatTestFactory.Options(),
            transport,
            IdentomatTestFactory.AlwaysAllowQuota(),
            Mock.Of<IHostedSessionFlow>(),
            logger: null,
            configuration: new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
}
