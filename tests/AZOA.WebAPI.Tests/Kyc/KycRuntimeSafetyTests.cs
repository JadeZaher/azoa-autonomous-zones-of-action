using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class KycRuntimeSafetyTests
{
    [Theory]
    [InlineData("Development", "Simulated", false, true)]
    [InlineData("Development", "Live", false, false)]
    [InlineData("Development", "Simulated", true, false)]
    [InlineData("Production", "Simulated", false, false)]
    public void ManualSimulation_RequiresEverySafetyCondition(
        string environmentName,
        string blockchainMode,
        bool realValueEnabled,
        bool expected)
    {
        var environment = Mock.Of<IHostEnvironment>(item => item.EnvironmentName == environmentName);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:Mode"] = blockchainMode,
                ["Blockchain:Bridge:RealValueEnabled"] = realValueEnabled.ToString(),
            })
            .Build();

        KycRuntimeSafety.IsManualSimulationAllowed(environment, configuration)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("Live", false)]
    [InlineData("Simulated", true)]
    public void ManualNodeAuthority_IsUnavailableWhenRuntimeIsNotSimulationSafe(
        string blockchainMode,
        bool realValueEnabled)
    {
        var settings = new KycSettings
        {
            Provider = "manual",
            ApprovalPolicy = new KycApprovalPolicySettings
            {
                PolicyVersion = "manual-v1",
                AssuranceLevel = "development-simulation",
                TrustedProviderKeys = ["manual"],
                AllowManualInDevelopment = true,
            },
        };
        var options = Options.Create(settings);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:Mode"] = blockchainMode,
                ["Blockchain:Bridge:RealValueEnabled"] = realValueEnabled.ToString(),
            })
            .Build();
        var registry = new KycProviderRegistry(
            Mock.Of<IKycControlStore>(),
            new ManualKycProviderService(options),
            new VeriffKycProviderService(),
            new GenericHostedKycProviderService(options),
            options,
            Mock.Of<IHostEnvironment>(item => item.EnvironmentName == Environments.Development),
            configuration);

        var result = registry.ResolveNodeDefault();

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.PolicyUnavailable);
        result.Message.Should().Be(KycRuntimeSafety.ManualSimulationUnavailable);
    }
}
