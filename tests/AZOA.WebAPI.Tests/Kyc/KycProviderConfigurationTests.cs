using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class KycProviderConfigurationTests
{
    [Fact]
    public async Task GenericHosted_WithCompleteConfiguration_RemainsFailClosedScaffold()
    {
        var provider = new GenericHostedKycProviderService(Options.Create(new KycSettings
        {
            Provider = "hosted",
            Hosted = new HostedKycSettings
            {
                ProviderName = "example-verify",
                BaseUrl = "https://verify.example",
                ApiKey = "secret",
                WebhookSecret = "webhook-secret",
                SessionPath = "/sessions",
                StatusPath = "/sessions/{sessionId}"
            }
        }));

        var capabilities = provider.GetCapabilities();
        var begin = await provider.BeginSessionAsync(Guid.NewGuid());

        capabilities.ProviderKey.Should().Be("example-verify");
        capabilities.Available.Should().BeFalse();
        capabilities.HostedVerification.Should().BeTrue();
        capabilities.UnavailableReason.Should().Contain("not production-ready");
        begin.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownProviderService_NeverFallsBackToManual()
    {
        var provider = new UnavailableKycProviderService();

        var capabilities = provider.GetCapabilities();
        var begin = await provider.BeginSessionAsync(Guid.NewGuid());

        capabilities.Available.Should().BeFalse();
        capabilities.AcceptsDocumentReferences.Should().BeFalse();
        capabilities.ProviderKey.Should().Be("unavailable");
        begin.IsError.Should().BeTrue();
    }

    [Fact]
    public void ApprovalPolicy_DefaultConfigurationFailsClosed()
    {
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(item => item.Provider).Returns(KycProvider.VERIFF);
        provider.SetupGet(item => item.ProviderKey).Returns("veriff");
        provider.Setup(item => item.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.VERIFF,
            ProviderKey = "veriff",
            Available = true,
        });

        var resolved = KycApprovalTrust.TryResolveCurrentProfile(
            provider.Object,
            new KycSettings(),
            Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == Environments.Production),
            out _,
            out _,
            out var failure);

        resolved.Should().BeFalse();
        failure.Should().Contain("explicitly");
    }
}
