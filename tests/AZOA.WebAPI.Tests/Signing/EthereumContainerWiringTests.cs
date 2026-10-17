using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// Ethereum's DI wiring, exercised through the same shapes <c>Program.cs</c> uses: a bare
/// <c>AddTransient&lt;EthereumProvider&gt;()</c>, an <c>ITransactionSigner</c> in the shared
/// factory, and a <c>BlockchainProviderRegistration</c> that hands the factory an UNBOUND
/// provider.
/// </summary>
/// <remarks>
/// The hazard this pins is a captive dependency: <see cref="IBlockchainProviderFactory"/> is
/// a SINGLETON that caches one provider per chain/network for the application lifetime, while
/// <see cref="IKeyCustodyService"/> is SCOPED. A provider that took custody directly would be
/// resolvable in a request scope and then held forever by the singleton cache — so every
/// resolution here happens from the ROOT provider under <c>ValidateScopes</c>.
/// </remarks>
public sealed class EthereumContainerWiringTests
{
    private const string SepoliaChainId = "11155111";

    [Fact]
    public void Ethereum_resolves_from_the_root_container_through_the_provider_factory()
    {
        using var root = BuildContainer();

        var provider = root.GetRequiredService<IBlockchainProviderFactory>()
            .GetProvider("Ethereum", ChainNetwork.Devnet);

        provider.Should().BeOfType<EthereumProvider>();
        provider.ChainType.Should().Be("Ethereum");
        provider.ActiveNetwork.Should().Be(ChainNetwork.Devnet);
        provider.RailId.Should().Be("Ethereum", "a chain rail names itself by its chain type");
        provider.ActiveEnvironment.Kind.Should().Be(RailEnvironmentKind.Test);
    }

    [Fact]
    public void The_factory_caches_one_bound_ethereum_instance_per_network()
    {
        using var root = BuildContainer();
        var factory = root.GetRequiredService<IBlockchainProviderFactory>();

        var devnet = factory.GetProvider("Ethereum", ChainNetwork.Devnet);
        var devnetAgain = factory.GetProvider("Ethereum", ChainNetwork.Devnet);
        var testnet = factory.GetProvider("Ethereum", ChainNetwork.Testnet);

        devnetAgain.Should().BeSameAs(devnet);
        testnet.Should().NotBeSameAs(devnet);
        testnet.ActiveNetwork.Should().Be(ChainNetwork.Testnet);
    }

    [Fact]
    public void The_registration_hands_the_factory_an_unbound_provider()
    {
        // Initialize is the factory's to call. A registration that returned a
        // pre-initialized instance would be rejected — this proves it is not one.
        using var root = BuildContainer();

        var fresh = root.GetRequiredService<EthereumProvider>();
        var bindItManually = () => fresh.Initialize(new BlockchainNetworkConfig { NodeUrl = "http://localhost:1/" },
            ChainNetwork.Devnet);

        bindItManually.Should().NotThrow("a freshly created provider must still be unbound");
    }

    [Fact]
    public void The_shared_signer_factory_serves_ethereum_alongside_the_other_chains()
    {
        using var root = BuildContainer();
        var signers = root.GetRequiredService<ITransactionSignerFactory>();

        signers.GetSigner("Ethereum").Should().BeOfType<EthereumTransactionSigner>();
        signers.GetSigner("Algorand").Should().BeOfType<AlgorandTransactionSigner>();
        signers.GetSigner("Solana").Should().BeOfType<SolanaTransactionSigner>();
    }

    [Fact]
    public void Ethereum_exposes_the_biginteger_native_value_module_through_the_factory()
    {
        using var root = BuildContainer();
        var factory = root.GetRequiredService<IBlockchainProviderFactory>();

        var provider = factory.GetProvider("Ethereum", ChainNetwork.Devnet);

        factory.TryGetModule<IEthereumValueModule>(provider, out var module).Should().BeTrue();
        module!.CapabilityName.Should().Be("Ethereum.Value");
    }

    /// <summary>Mirrors the Ethereum half of Program.cs, under full scope validation.</summary>
    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(BuildConfig());

        services.AddSingleton<ITransactionSigner, AlgorandTransactionSigner>();
        services.AddSingleton<ITransactionSigner, SolanaTransactionSigner>();
        services.AddSingleton<ITransactionSigner, EthereumTransactionSigner>();
        services.AddSingleton<ITransactionSignerFactory>(sp =>
            new TransactionSignerFactory(sp.GetServices<ITransactionSigner>()));

        // SCOPED, exactly as in production — the reason the provider must not take it
        // as a constructor parameter.
        services.AddScoped(_ => new Mock<IKeyCustodyService>().Object);

        services.AddTransient<EthereumProvider>();
        services.AddSingleton<IBlockchainProviderFactory>(sp =>
            new BlockchainProviderFactory(
                new[] { new BlockchainProviderRegistration("Ethereum", sp.GetRequiredService<EthereumProvider>) },
                sp.GetRequiredService<IConfiguration>()));

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = "Ethereum",
                ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://127.0.0.1:1/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
                ["Blockchain:Chains:0:Testnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Testnet:NodeUrl"] = "http://127.0.0.1:2/",
                ["Blockchain:Chains:0:Testnet:TimeoutMs"] = "2000",
                ["Blockchain:Chains:0:Devnet:ChainId"] = SepoliaChainId,
            })
            .Build();
}
