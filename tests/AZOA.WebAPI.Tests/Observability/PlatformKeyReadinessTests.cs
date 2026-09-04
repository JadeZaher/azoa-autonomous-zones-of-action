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
using Solnet.Wallet.Bip39;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Observability;

/// <summary>
/// Real-value readiness must prove the platform signing key for EVERY enabled chain, not
/// just Algorand — otherwise a missing Solana/Ethereum platform key first shows up as a
/// failed value operation. A chain configured but disabled on every network is not a
/// readiness problem and must not fail the probe.
/// </summary>
public sealed class PlatformKeyReadinessTests
{
    private const string EthereumTestPrivateKey =
        "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    [Fact]
    public async Task Enabled_solana_without_a_platform_key_fails_readiness()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Solana", enabled: true);

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("AZOA:Solana:PlatformMnemonic is required");
    }

    [Fact]
    public async Task Disabled_solana_without_a_platform_key_stays_ready()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Solana", enabled: false);

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Enabled_solana_with_a_valid_platform_key_stays_ready()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Solana", enabled: true);
        values["AZOA:Solana:PlatformMnemonic"] = new Mnemonic(WordList.English, WordCount.Twelve).ToString();

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Enabled_solana_with_a_malformed_platform_key_fails_readiness()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Solana", enabled: true);
        values["AZOA:Solana:PlatformMnemonic"] = "clearly not a bip39 phrase";

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("must be a valid Solana BIP-39 mnemonic");
        result.Description.Should().NotContain("clearly not a bip39 phrase",
            "a readiness description is surfaced widely and must never echo a configured secret");
    }

    [Fact]
    public async Task Enabled_ethereum_without_a_platform_key_fails_readiness()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Ethereum", enabled: true);

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("AZOA:Ethereum:PlatformPrivateKey is required");
    }

    [Fact]
    public async Task Enabled_ethereum_with_a_valid_platform_key_stays_ready()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Ethereum", enabled: true);
        values["AZOA:Ethereum:PlatformPrivateKey"] = EthereumTestPrivateKey;

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Enabled_chain_with_no_registered_derivation_fails_readiness_naming_it()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Bitcoin", enabled: true);

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Bitcoin");
        result.Description.Should().Contain("no registered platform signing key derivation");
    }

    [Fact]
    public async Task Mainnet_only_enablement_still_requires_the_platform_key()
    {
        var values = BaseConfiguration();
        values["Blockchain:Chains:1:ChainType"] = "Solana";
        values["Blockchain:Chains:1:Mainnet:IsEnabled"] = "true";

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("AZOA:Solana:PlatformMnemonic is required");
    }

    [Fact]
    public async Task Real_value_disabled_still_short_circuits_before_any_platform_key_check()
    {
        var values = BaseConfiguration();
        AddChain(values, index: 1, chainType: "Solana", enabled: true);
        values["Blockchain:Bridge:RealValueEnabled"] = "false";

        var result = await Check(values);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    // ─── Helpers ───

    private static async Task<HealthCheckResult> Check(Dictionary<string, string?> values)
    {
        var check = new RealValueReadinessHealthCheck(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            ReadyProviderFactory(),
            ReadyKycProvider(),
            Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == Environments.Production));

        return await check.CheckHealthAsync(new HealthCheckContext());
    }

    private static void AddChain(Dictionary<string, string?> values, int index, string chainType, bool enabled)
    {
        values[$"Blockchain:Chains:{index}:ChainType"] = chainType;
        values[$"Blockchain:Chains:{index}:Devnet:IsEnabled"] = enabled ? "true" : "false";
        values[$"Blockchain:Chains:{index}:Testnet:IsEnabled"] = enabled ? "true" : "false";
        values[$"Blockchain:Chains:{index}:Mainnet:IsEnabled"] = "false";
    }

    /// <summary>An otherwise launch-ready Algorand-only real-value configuration.</summary>
    private static Dictionary<string, string?> BaseConfiguration() => new()
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

    private static IBlockchainProviderFactory ReadyProviderFactory()
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(item => item.ChainType).Returns("Algorand");
        provider.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Testnet);
        provider.WithBridgeLegs();

        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(item => item.GetProvider("Algorand", ChainNetwork.Testnet)).Returns(provider.Object);
        return factory.Object;
    }

    private static IKycProviderService ReadyKycProvider()
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
        return provider.Object;
    }
}
