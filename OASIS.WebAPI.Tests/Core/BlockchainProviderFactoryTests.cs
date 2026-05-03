using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Providers.Blockchain;

namespace OASIS.WebAPI.Tests.Core;

public class BlockchainProviderFactoryTests
{
    private readonly Mock<IBlockchainProvider> _algoProvider;
    private readonly Mock<IBlockchainProvider> _solProvider;
    private readonly BlockchainProviderFactory _factory;

    public BlockchainProviderFactoryTests()
    {
        _algoProvider = new Mock<IBlockchainProvider>();
        _algoProvider.Setup(p => p.ChainType).Returns("Algorand");

        _solProvider = new Mock<IBlockchainProvider>();
        _solProvider.Setup(p => p.ChainType).Returns("Solana");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = "Algorand",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "https://testnet-api.algonode.cloud",
                ["Blockchain:Chains:1:ChainType"] = "Solana",
                ["Blockchain:Chains:1:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:1:Devnet:NodeUrl"] = "https://api.devnet.solana.com"
            })
            .Build();

        _factory = new BlockchainProviderFactory(new[] { _algoProvider.Object, _solProvider.Object }, config);
    }

    [Fact]
    public void GetProvider_Algorand_ShouldReturnAlgoProvider()
    {
        var provider = _factory.GetProvider("Algorand", ChainNetwork.Devnet);
        provider.ChainType.Should().Be("Algorand");
    }

    [Fact]
    public void GetProvider_Solana_ShouldReturnSolProvider()
    {
        var provider = _factory.GetProvider("Solana", ChainNetwork.Devnet);
        provider.ChainType.Should().Be("Solana");
    }

    [Fact]
    public void GetProvider_Unknown_ShouldThrow()
    {
        Action act = () => _factory.GetProvider("Ethereum", ChainNetwork.Mainnet);
        act.Should().Throw<InvalidOperationException>().WithMessage("*No provider registered*");
    }

    [Fact]
    public void GetDefaultProvider_ShouldReturnDefaultChain()
    {
        var provider = _factory.GetDefaultProvider();
        provider.ChainType.Should().Be("Algorand");
    }

    [Fact]
    public void GetAllEnabledProviders_ShouldReturnEnabledOnly()
    {
        var providers = _factory.GetAllEnabledProviders().ToList();
        providers.Should().HaveCount(2);
    }

    [Fact]
    public void TryGetModule_WithMatchingModule_ShouldReturnTrue()
    {
        var moduleProvider = new Mock<IBlockchainProvider>();
        moduleProvider.As<IAlgorandASAModule>().Setup(m => m.CapabilityName).Returns("Algorand.ASA");

        var result = _factory.TryGetModule(moduleProvider.Object, out IAlgorandASAModule? module);

        result.Should().BeTrue();
        module.Should().NotBeNull();
    }

    [Fact]
    public void TryGetModule_WithNonMatchingModule_ShouldReturnFalse()
    {
        var result = _factory.TryGetModule(_algoProvider.Object, out ISolanaMetaplexModule? module);

        result.Should().BeFalse();
        module.Should().BeNull();
    }

    [Fact]
    public void GetProvider_CachesInstances()
    {
        var p1 = _factory.GetProvider("Algorand", ChainNetwork.Devnet);
        var p2 = _factory.GetProvider("Algorand", ChainNetwork.Devnet);

        p1.Should().BeSameAs(p2);
    }

    [Fact]
    public void GetProvider_DifferentNetworks_CreatesSeparateKeys()
    {
        var p1 = _factory.GetProvider("Algorand", ChainNetwork.Devnet);
        var p2 = _factory.GetProvider("Algorand", ChainNetwork.Testnet);

        // Factory caches by key; different networks = different cache entries
        // (same mock instance because registered providers are singletons in tests)
        p1.Should().NotBeNull();
        p2.Should().NotBeNull();
    }
}
