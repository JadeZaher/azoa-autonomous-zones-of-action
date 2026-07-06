using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// db-only-null-provider track: the global <c>Blockchain:Mode</c> flag selects
/// the simulated provider through the existing factory, with no call-site change.
/// </summary>
public class BlockchainProviderFactorySelectionTests
{
    // Real providers used as the "Live" registrants. Simulated is always present
    // (it is registered in DI in every environment); the flag decides selection.
    private static IEnumerable<IBlockchainProvider> Registrants(IConfiguration config) => new IBlockchainProvider[]
    {
        new SolanaProvider(config, NullLogger<SolanaProvider>.Instance),
        new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance),
    };

    private static IConfiguration ConfigWithMode(string mode)
    {
        // Load the REAL appsettings (per config-driven-calls) then overlay only the
        // Mode flag so the Chains[] section is the genuine shipped configuration.
        return new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Blockchain:Mode"] = mode })
            .Build();
    }

    [Fact]
    public void SimulatedMode_GetProvider_ReturnsSimulatedRegardlessOfRequestedChain()
    {
        var config = ConfigWithMode("Simulated");
        var factory = new BlockchainProviderFactory(Registrants(config), config);

        // Ask for a real chain — simulated mode overrides it.
        var provider = factory.GetProvider("Solana", ChainNetwork.Devnet);

        provider.Should().BeOfType<SimulatedBlockchainProvider>();
        provider.ChainType.Should().Be("Simulated");
    }

    [Fact]
    public void SimulatedMode_GetDefaultProvider_ReturnsSimulated()
    {
        var config = ConfigWithMode("Simulated");
        var factory = new BlockchainProviderFactory(Registrants(config), config);

        factory.GetDefaultProvider().Should().BeOfType<SimulatedBlockchainProvider>();
    }

    [Fact]
    public void LiveMode_GetProvider_ReturnsRealProviderForRequestedChain()
    {
        var config = ConfigWithMode("Live");
        var factory = new BlockchainProviderFactory(Registrants(config), config);

        var provider = factory.GetProvider("Solana", ChainNetwork.Devnet);

        provider.Should().BeOfType<SolanaProvider>();
        provider.ChainType.Should().Be("Solana");
    }

    [Fact]
    public void LiveMode_UnregisteredChain_StillThrows()
    {
        var config = ConfigWithMode("Live");
        var factory = new BlockchainProviderFactory(Registrants(config), config);

        var act = () => factory.GetProvider("Bitcoin", ChainNetwork.Devnet);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No provider registered for chain type: Bitcoin*");
    }

    // H1: Blockchain:Mode=Simulated must never reach a Production host.
    [Fact]
    public void GuardAgainstSimulatedModeInProduction_ProductionAndSimulated_Throws()
    {
        var config = ConfigWithMode("Simulated");

        var act = () => BlockchainProviderFactory.GuardAgainstSimulatedModeInProduction(
            config, isProductionEnvironment: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Blockchain:Mode*Simulated*Production*");
    }

    [Theory]
    [InlineData(false)] // Dev/IntegrationTest/other non-Production environments
    public void GuardAgainstSimulatedModeInProduction_NonProductionAndSimulated_Allowed(bool isProductionEnvironment)
    {
        var config = ConfigWithMode("Simulated");

        var act = () => BlockchainProviderFactory.GuardAgainstSimulatedModeInProduction(
            config, isProductionEnvironment);

        act.Should().NotThrow();
    }

    [Fact]
    public void GuardAgainstSimulatedModeInProduction_ProductionAndLive_Allowed()
    {
        var config = ConfigWithMode("Live");

        var act = () => BlockchainProviderFactory.GuardAgainstSimulatedModeInProduction(
            config, isProductionEnvironment: true);

        act.Should().NotThrow();
    }
}
