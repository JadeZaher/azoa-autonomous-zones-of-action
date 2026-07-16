using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain.Base;
using AZOA.WebAPI.Providers.Blockchain;
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
    // Every registration creates a fresh mutable provider; network binding belongs
    // to the factory, never to a singleton DI provider.
    private static IEnumerable<BlockchainProviderRegistration> Registrants(IConfiguration config) =>
    [
        new("Solana", () => new SolanaProvider(config, NullLogger<SolanaProvider>.Instance)),
        new("Simulated", () => new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance)),
    ];

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

        act.Should().Throw<BlockchainProviderNotFoundException>()
            .WithMessage("*No provider is registered for chain type 'Bitcoin'*");
    }

    [Fact]
    public void LiveMode_DisabledNetwork_RejectsBeforeCreatingProvider()
    {
        var config = ConfigWithMode("Live");
        var created = 0;
        var factory = new BlockchainProviderFactory(
        [new BlockchainProviderRegistration("Solana", () =>
        {
            created++;
            return new SolanaProvider(config, NullLogger<SolanaProvider>.Instance);
        })], config);

        var act = () => factory.GetProvider("Solana", ChainNetwork.Mainnet);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Network Mainnet is disabled for chain 'Solana'*");
        created.Should().Be(0);
    }

    [Fact]
    public void LiveMode_UndefinedNetwork_RejectsBeforeCreatingProvider()
    {
        var config = ConfigWithTwoSolanaNetworks();
        var created = 0;
        var factory = new BlockchainProviderFactory(
        [new BlockchainProviderRegistration("Solana", () =>
        {
            created++;
            return new SolanaProvider(config, NullLogger<SolanaProvider>.Instance);
        })], config);

        var act = () => factory.GetProvider("Solana", (ChainNetwork)99);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Unsupported blockchain network*");
        created.Should().Be(0);
    }

    [Fact]
    public async Task LiveFactory_ConcurrentResolution_CreatesOneIndependentInstancePerNetwork()
    {
        var config = ConfigWithTwoSolanaNetworks();
        var created = 0;
        var factory = new BlockchainProviderFactory(
        [
            new BlockchainProviderRegistration("Solana", () =>
            {
                Interlocked.Increment(ref created);
                return new SolanaProvider(config, NullLogger<SolanaProvider>.Instance);
            }),
        ], config);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(index => Task.Run(() => factory.GetProvider(
                    "solana",
                    index % 2 == 0 ? ChainNetwork.Devnet : ChainNetwork.Testnet))));

        var devnet = factory.GetProvider("Solana", ChainNetwork.Devnet);
        var testnet = factory.GetProvider("Solana", ChainNetwork.Testnet);

        created.Should().Be(2, "each network has exactly one lazily created provider instance");
        devnet.Should().NotBeSameAs(testnet);
        devnet.ActiveNetwork.Should().Be(ChainNetwork.Devnet);
        testnet.ActiveNetwork.Should().Be(ChainNetwork.Testnet);
        results.Where((_, index) => index % 2 == 0).Should().OnlyContain(provider => ReferenceEquals(provider, devnet));
        results.Where((_, index) => index % 2 != 0).Should().OnlyContain(provider => ReferenceEquals(provider, testnet));
    }

    [Fact]
    public void LiveFactory_InitializingSecondNetwork_DoesNotRetargetFirstNetwork()
    {
        var config = ConfigWithTwoSolanaNetworks();
        var factory = new BlockchainProviderFactory(
        [new BlockchainProviderRegistration(
            "Solana",
            () => new SolanaProvider(config, NullLogger<SolanaProvider>.Instance))],
            config);

        var devnet = factory.GetProvider("Solana", ChainNetwork.Devnet);
        var testnet = factory.GetProvider("Solana", ChainNetwork.Testnet);

        devnet.Should().NotBeSameAs(testnet);
        devnet.ActiveNetwork.Should().Be(ChainNetwork.Devnet);
        testnet.ActiveNetwork.Should().Be(ChainNetwork.Testnet);
    }

    [Fact]
    public void FactoryBinding_InitializesBaseProviderOnceAndRejectsExternalRebind()
    {
        var config = ConfigWithEnabledNetwork("Tracking");
        TrackingProvider? created = null;
        var factory = new BlockchainProviderFactory(
        [new BlockchainProviderRegistration("Tracking", () => created = new TrackingProvider(config))], config);

        var bound = factory.GetProvider("Tracking", ChainNetwork.Devnet).Should().BeOfType<TrackingProvider>().Subject;
        factory.GetProvider("Tracking", ChainNetwork.Devnet).Should().BeSameAs(bound);

        created.Should().BeSameAs(bound);
        bound.InitializeCount.Should().Be(1);
        bound.ActiveNetwork.Should().Be(ChainNetwork.Devnet);
        var act = () => bound.Initialize(new BlockchainNetworkConfig(), ChainNetwork.Testnet);
        act.Should().Throw<InvalidOperationException>().WithMessage("*factory-bound*");
    }

    [Fact]
    public void StandaloneProvider_InitializesOnlyOnce()
    {
        var provider = new TrackingProvider(ConfigWithEnabledNetwork("Tracking"));

        provider.Initialize(new BlockchainNetworkConfig { NodeUrl = "https://devnet.example" }, ChainNetwork.Devnet);

        var act = () => provider.Initialize(
            new BlockchainNetworkConfig { NodeUrl = "https://testnet.example" },
            ChainNetwork.Testnet);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already initialized*");
        provider.InitializeCount.Should().Be(1);
        provider.ActiveNetwork.Should().Be(ChainNetwork.Devnet);
    }

    [Fact]
    public void FactoryCreator_UsesTransientDiResolutionOncePerNetwork()
    {
        var config = ConfigWithEnabledNetwork("Tracking");
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddTransient(_ =>
        {
            resolutions++;
            return new TrackingProvider(config);
        });
        using var serviceProvider = services.BuildServiceProvider();
        var factory = new BlockchainProviderFactory(
        [new BlockchainProviderRegistration(
            "Tracking",
            serviceProvider.GetRequiredService<TrackingProvider>)], config);

        var devnet = factory.GetProvider("Tracking", ChainNetwork.Devnet)
            .Should().BeOfType<TrackingProvider>().Subject;
        factory.GetProvider("Tracking", ChainNetwork.Devnet).Should().BeSameAs(devnet);
        var testnet = factory.GetProvider("Tracking", ChainNetwork.Testnet)
            .Should().BeOfType<TrackingProvider>().Subject;

        resolutions.Should().Be(2);
        devnet.InitializeCount.Should().Be(1);
        testnet.InitializeCount.Should().Be(1);
        testnet.Should().NotBeSameAs(devnet);
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

    private static IConfiguration ConfigWithTwoSolanaNetworks()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = "Solana",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Mode"] = "Live",
                ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "https://api.devnet.solana.com",
                ["Blockchain:Chains:0:Testnet:NodeUrl"] = "https://api.testnet.solana.com",
                ["Blockchain:Chains:0:Mainnet:NodeUrl"] = "https://api.mainnet-beta.solana.com",
            })
            .Build();

    private static IConfiguration ConfigWithEnabledNetwork(string chainType)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = chainType,
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Mode"] = "Live",
                ["Blockchain:Chains:0:ChainType"] = chainType,
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "https://devnet.example",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Testnet:NodeUrl"] = "https://testnet.example",
                ["Blockchain:Chains:0:Testnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Mainnet:NodeUrl"] = "https://mainnet.example",
                ["Blockchain:Chains:0:Mainnet:IsEnabled"] = "false",
            })
            .Build();

    private sealed class TrackingProvider : BaseBlockchainProvider
    {
        public TrackingProvider(IConfiguration config)
            : base(config, NullLogger<TrackingProvider>.Instance)
        {
        }

        public override string ChainType => "Tracking";
        public int InitializeCount { get; private set; }

        protected override void OnInitialize(BlockchainNetworkConfig config, ChainNetwork network)
        {
            InitializeCount++;
        }
    }
}
