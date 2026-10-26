// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyb;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// The local dev Identomat sidecar (tools/dev-kyc-vendor) is reachable ONLY on a Development +
/// Simulated host that opts in; every other host keeps refusing loopback/plain-http vendor origins.
/// Driven through the public availability seams the KYC/KYB gates read. See
/// <c>tools/dev-kyc-vendor/AGENTS.md</c> §origin-allowance.
/// </summary>
public sealed class IdentomatDevVendorOriginTests
{
    private const string SidecarApi = "http://127.0.0.1:8031";
    private const string SidecarWidget = "http://127.0.0.1:8032";

    /// <summary>(environment, opt-in flag, chain mode, real-value-bridge enabled, expected availability).
    /// L1: every seam driven off this table also exercises a live chain mode and a real-value
    /// bridge, not just the environment/opt-in axis, so the three callers below can never
    /// silently diverge on which conditions gate the allowance.</summary>
    public static TheoryData<string, bool, string, bool, bool> Hosts => new()
    {
        { Environments.Production, true, "Simulated", false, false },
        { Environments.Staging, true, "Simulated", false, false },
        { Environments.Production, false, "Simulated", false, false },
        { Environments.Development, false, "Simulated", false, false },
        { Environments.Development, true, "Simulated", false, true },
        { Environments.Development, true, "Live", false, false },
        { Environments.Development, true, "Simulated", true, false },
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Kyc_adapter_admits_a_loopback_sidecar_only_on_an_opted_in_development_host(
        string environment, bool optedIn, string chainMode, bool realValueEnabled, bool expected)
    {
        var provider = Kyc(Sidecar(), environment, Configuration(optedIn, chainMode, realValueEnabled));

        var hosted = provider.GetHostedCapabilities();
        hosted.Available.Should().Be(expected, string.Join(", ", hosted.MissingConfigurationKeys));
        provider.GetCapabilities().Available.Should().Be(expected);
        if (!expected)
        {
            hosted.MissingConfigurationKeys.Should().Contain(
                [IdentomatKycSettings.BeginBaseUrlName, IdentomatKycSettings.ResultBaseUrlName]);
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Registry_readiness_follows_the_same_rule(
        string environment, bool optedIn, string chainMode, bool realValueEnabled, bool expected)
    {
        var settings = IdentomatTestFactory.Options(Sidecar());
        settings.Value.ApprovalPolicy = new KycApprovalPolicySettings
        {
            PolicyVersion = "identomat-hosted-v1",
            AssuranceLevel = "document-and-liveness",
            TrustedProviderKeys = [IdentomatKycProviderService.ProviderKeyValue],
        };
        var host = Host(environment);
        var configuration = Configuration(optedIn, chainMode, realValueEnabled);
        var registry = new KycProviderRegistry(
            Mock.Of<IKycControlStore>(),
            new ManualKycProviderService(settings),
            HostedIdentityTestFactory.Veriff(),
            HostedIdentityTestFactory.Generic(settings),
            IdentomatTestFactory.Create(settings, configuration: configuration, environment: host),
            settings,
            host,
            configuration);

        var evaluated = registry.EvaluateCandidate(new KycProviderProfile
        {
            Id = IdentomatKycProviderService.ProviderKeyValue,
            DisplayName = "Identomat",
            AdapterKey = IdentomatKycProviderService.ProviderKeyValue,
            Enabled = true,
            PolicyVersion = "identomat-hosted-v1",
            AssuranceLevel = "document-and-liveness",
            Version = 1,
            TrustRevision = 1,
        });

        evaluated.ReadinessCode.Should().Be(expected
            ? KycProviderReadinessCodes.Ready
            : KycProviderReadinessCodes.AdapterUnavailable);
        evaluated.MissingConfigurationKeys.Contains(IdentomatKycSettings.BeginBaseUrlName).Should().Be(!expected);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void Kyb_adapter_follows_the_same_rule(
        string environment, bool optedIn, string chainMode, bool realValueEnabled, bool expected)
    {
        var provider = new IdentomatKybProvider(
            IdentomatTestFactory.Options(Sidecar()),
            Options.Create(new KybSettings { ConfigId = "kyb-config", PolicyVersion = "kyb-v1" }),
            Mock.Of<IIdentomatHttpTransport>(),
            Host(environment),
            Configuration(optedIn, chainMode, realValueEnabled));

        var capabilities = provider.GetHostedCapabilities();
        capabilities.Available.Should().Be(expected, string.Join(", ", capabilities.MissingConfigurationKeys));
    }

    [Theory]
    [InlineData("http://external-api.identomat.com")]
    [InlineData("http://10.0.0.5:8031")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://user:secret@127.0.0.1:8031")]
    [InlineData("http://127.0.0.1:8031/?probe=1")]
    [InlineData("http://127.0.0.1:8031#fragment")]
    [InlineData("ftp://127.0.0.1:8031")]
    public void The_allowance_admits_loopback_only_never_other_insecure_origins(string origin)
    {
        var settings = Sidecar();
        settings.BeginBaseUrl = origin;

        var hosted = Kyc(settings, Environments.Development, Configuration(optedIn: true)).GetHostedCapabilities();

        hosted.Available.Should().BeFalse();
        hosted.MissingConfigurationKeys.Should().Contain(IdentomatKycSettings.BeginBaseUrlName);
    }

    [Theory]
    [InlineData(SidecarWidget + "/?session_token=idm-1", true)]
    [InlineData("http://127.0.0.1:9999/?session_token=idm-1", false)]
    [InlineData("http://widget.identomat.com/?session_token=idm-1", false)]
    public async Task Begin_accepts_a_loopback_widget_redirect_only_on_the_configured_result_host(string redirect, bool accepted)
    {
        var transport = new Mock<IIdentomatHttpTransport>();
        transport
            .Setup(item => item.PostAsync<IdentomatBeginRequestWithSessionOverrides, IdentomatBeginResponse>(
                SidecarApi, "/begin", It.IsAny<IdentomatBeginRequestWithSessionOverrides>(), "begin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IdentomatBeginResponse>.Success(new IdentomatBeginResponse { Id = "idm-1", Url = redirect }));
        var provider = IdentomatTestFactory.Create(
            IdentomatTestFactory.Options(Sidecar()),
            transport.Object,
            configuration: Configuration(optedIn: true),
            environment: Host(Environments.Development));

        var created = await provider.CreateHostedSessionAsync(new HostedSessionRequest
        {
            SubjectUserId = Guid.NewGuid(),
            AdmissionKey = "kyc:identomat:test",
        });

        created.IsError.Should().Be(!accepted, created.Message);
        if (accepted)
            created.Result!.RedirectUrl.Should().Be(redirect);
    }

    /// <summary>M1 (A4 fix batch): the KYB adapter must pin the loopback redirect to the
    /// configured result host exactly like the KYC adapter does above — an off-port loopback
    /// (e.g. :9999, some other local service) must be refused even though it is loopback.</summary>
    [Theory]
    [InlineData(SidecarWidget + "/?session_token=idm-kyb-1", true)]
    [InlineData("http://127.0.0.1:9999/?session_token=idm-kyb-1", false)]
    [InlineData("http://widget.identomat.com/?session_token=idm-kyb-1", false)]
    public async Task Kyb_begin_accepts_a_loopback_widget_redirect_only_on_the_configured_result_host(string redirect, bool accepted)
    {
        var transport = new Mock<IIdentomatHttpTransport>();
        transport
            .Setup(item => item.PostAsync<Dictionary<string, object>, IdentomatBeginResponse>(
                SidecarApi, "/begin", It.IsAny<Dictionary<string, object>>(), "kyb-begin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IdentomatBeginResponse>.Success(new IdentomatBeginResponse { Id = "idm-kyb-1", Url = redirect }));
        var tenantId = Guid.NewGuid();
        var provider = new IdentomatKybProvider(
            IdentomatTestFactory.Options(Sidecar()),
            Options.Create(new KybSettings { ConfigId = "kyb-config", PolicyVersion = "kyb-v1" }),
            transport.Object,
            Host(Environments.Development),
            Configuration(optedIn: true));

        var created = await provider.CreateHostedSessionAsync(new HostedSessionRequest
        {
            FlowKind = HostedSessionFlowKinds.Kyb,
            SubjectUserId = tenantId,
            TenantId = tenantId,
            AdmissionKey = "kyb:identomat:test",
        });

        created.IsError.Should().Be(!accepted, created.Message);
        if (accepted)
            created.Result!.RedirectUrl.Should().Be(redirect);
    }

    private static IdentomatKycSettings Sidecar()
    {
        var settings = IdentomatTestFactory.CompleteSettings();
        settings.BeginBaseUrl = SidecarApi;
        settings.ResultBaseUrl = SidecarWidget;
        return settings;
    }

    private static IdentomatKycProviderService Kyc(IdentomatKycSettings settings, string environment, IConfiguration configuration)
        => IdentomatTestFactory.Create(
            IdentomatTestFactory.Options(settings),
            configuration: configuration,
            environment: Host(environment));

    private static IHostEnvironment Host(string environment)
        => Mock.Of<IHostEnvironment>(item => item.EnvironmentName == environment);

    private static IConfiguration Configuration(bool optedIn, string chainMode = "Simulated", bool realValueEnabled = false)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:Mode"] = chainMode,
                ["Blockchain:Bridge:RealValueEnabled"] = realValueEnabled.ToString(),
                [KycRuntimeSafety.InsecureDevVendorConfigurationKey] = optedIn ? "true" : "false",
            })
            .Build();
}
