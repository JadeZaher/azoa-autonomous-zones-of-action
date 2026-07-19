using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Observability;
using AZOA.WebAPI.Providers.Blockchain;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AZOA.WebAPI.Tests.Observability;

public sealed class RealValueReadinessHealthCheckTests
{
    [Fact]
    public async Task DisabledRealValue_IsHealthyWithoutCustodyConfiguration()
    {
        var factory = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        var check = new RealValueReadinessHealthCheck(
            BuildConfiguration(new() { ["Blockchain:Bridge:RealValueEnabled"] = "false" }),
            factory.Object,
            new Mock<IKycProviderService>(MockBehavior.Strict).Object,
            HostEnvironment(Environments.Production));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("AZOA:Algorand:PlatformMnemonic", "", "PlatformMnemonic")]
    [InlineData("AZOA:Algorand:PlatformMnemonic", "not a mnemonic", "valid Algorand mnemonic")]
    [InlineData("Blockchain:Wormhole:BridgeVaults:Algorand:VaultAddress", "invalid", "Algorand address")]
    [InlineData("Blockchain:Chains:0:Testnet:IsEnabled", "false", "must be enabled")]
    [InlineData("Blockchain:Chains:0:Testnet:NodeUrl", "not-a-url", "absolute HTTP(S) URL")]
    [InlineData("Blockchain:Wormhole:DefaultMode", "Wormhole", "must be Trusted")]
    [InlineData("Kyc:Provider", "", "explicitly select")]
    [InlineData("Kyc:SubmissionExpiryDays", "0", "greater than zero")]
    [InlineData("Kyc:ApprovalPolicy:PolicyVersion", "", "approval policy is unavailable")]
    [InlineData("Kyc:ApprovalPolicy:AssuranceLevel", "", "approval policy is unavailable")]
    [InlineData("Kyc:ApprovalPolicy:TrustedProviderKeys:0", "retired-provider", "approval policy is unavailable")]
    public async Task EnabledRealValue_WithIncompletePrerequisite_IsUnhealthy(
        string key,
        string value,
        string expectedFailure)
    {
        var values = CompleteRealValueConfiguration();
        values[key] = value;
        var factory = ReadyProviderFactory();
        var check = new RealValueReadinessHealthCheck(
            BuildConfiguration(values),
            factory.Object,
            ReadyKycProvider().Object,
            HostEnvironment(Environments.Production));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(expectedFailure);
    }

    [Fact]
    public async Task EnabledRealValue_WithCompleteExplicitConfiguration_IsHealthy()
    {
        var factory = ReadyProviderFactory();
        var check = new RealValueReadinessHealthCheck(
            BuildConfiguration(CompleteRealValueConfiguration()),
            factory.Object,
            ReadyKycProvider().Object,
            HostEnvironment(Environments.Production));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        factory.Verify(provider => provider.GetProvider("Algorand", ChainNetwork.Testnet), Times.Once);
    }

    [Fact]
    public async Task EnabledRealValue_WithUnavailableKycAuthority_IsUnhealthy()
    {
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(item => item.Provider).Returns(KycProvider.VERIFF);
        provider.SetupGet(item => item.ProviderKey).Returns("veriff");
        provider.Setup(item => item.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.VERIFF,
            ProviderKey = "veriff",
            Available = false,
        });
        var check = new RealValueReadinessHealthCheck(
            BuildConfiguration(CompleteRealValueConfiguration()),
            ReadyProviderFactory().Object,
            provider.Object,
            HostEnvironment(Environments.Production));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("KYC authority or approval policy is unavailable");
    }

    [Fact]
    public async Task EnabledRealValue_RejectsManualAuthorityEvenInDevelopment()
    {
        var values = CompleteRealValueConfiguration();
        values["Kyc:Provider"] = "manual";
        values["Kyc:ApprovalPolicy:TrustedProviderKeys:0"] = "manual";
        values["Kyc:ApprovalPolicy:AllowManualInDevelopment"] = "true";
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(item => item.Provider).Returns(KycProvider.MANUAL);
        provider.SetupGet(item => item.ProviderKey).Returns("manual");
        var check = new RealValueReadinessHealthCheck(
            BuildConfiguration(values),
            ReadyProviderFactory().Object,
            provider.Object,
            HostEnvironment(Environments.Development));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Manual KYC simulation cannot authorize real-value operations");
    }

    private static Mock<IBlockchainProviderFactory> ReadyProviderFactory()
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(item => item.ChainType).Returns("Algorand");
        provider.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Testnet);
        provider.SetupGet(item => item.SupportsBridging).Returns(true);

        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(item => item.GetProvider("Algorand", ChainNetwork.Testnet))
            .Returns(provider.Object);
        return factory;
    }

    private static Mock<IKycProviderService> ReadyKycProvider()
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
        return provider;
    }

    private static Dictionary<string, string?> CompleteRealValueConfiguration() => new()
    {
        ["Blockchain:Bridge:RealValueEnabled"] = "true",
        ["Blockchain:Mode"] = "Live",
        ["Blockchain:DefaultChain"] = "Algorand",
        ["Blockchain:DefaultNetwork"] = "Testnet",
        ["Blockchain:Wormhole:DefaultMode"] = "Trusted",
        ["Blockchain:Wormhole:BridgeVaults:Algorand:VaultAddress"] =
            new Algorand.Algod.Model.Account().Address.EncodeAsString(),
        ["Blockchain:Chains:0:ChainType"] = "Algorand",
        ["Blockchain:Chains:0:Testnet:IsEnabled"] = "true",
        ["Blockchain:Chains:0:Testnet:NodeUrl"] = "https://testnet-api.algonode.cloud",
        ["AZOA:Algorand:PlatformMnemonic"] = new Algorand.Algod.Model.Account().ToMnemonic(),
        ["Kyc:Provider"] = "veriff",
        ["Kyc:SubmissionExpiryDays"] = "365",
        ["Kyc:ApprovalPolicy:PolicyVersion"] = "production-v1",
        ["Kyc:ApprovalPolicy:AssuranceLevel"] = "substantial",
        ["Kyc:ApprovalPolicy:TrustedProviderKeys:0"] = "veriff",
    };

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment HostEnvironment(string name)
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == name);
}
