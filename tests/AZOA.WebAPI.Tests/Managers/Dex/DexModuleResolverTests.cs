using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Dex;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>Capability discovery lists only structurally complete modules per concrete network, never a fixed chain list.</summary>
public sealed class DexModuleResolverTests
{
    private delegate bool TryRail(string railId, RailEnvironment environment, out IValueRailProvider? provider);
    private delegate bool TryModule(IBlockchainProvider provider, out IDexTransactionModule? module);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Discovery_reports_declared_modules_with_their_authoritative_network_identity(bool declared, bool identityAvailable)
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(item => item.ChainType).Returns("Ethereum");
        var module = new Mock<IDexTransactionModule>();
        module.SetupGet(item => item.ChainType).Returns("Ethereum");
        module.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Devnet);
        module.SetupGet(item => item.DexCapability).Returns(new DexCapability("UniswapV3", "classic-exact-input/1", true, false, true, true, true, ["one pool"]));
        module.Setup(item => item.DescribeDexNetworkAsync(It.IsAny<CancellationToken>())).ReturnsAsync(identityAvailable
            ? AZOAResult<DexNetwork>.Success(new("Ethereum", "Devnet", "eip155:31337"))
            : AZOAResult<DexNetwork>.FailureWithCode("node unreachable", AzoaErrorCodes.DependencyUnavailable));
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(item => item.GetAllEnabledProviders()).Returns([provider.Object]);
        factory.Setup(item => item.TryGetRailProvider("Ethereum", It.IsAny<RailEnvironment>(), out It.Ref<IValueRailProvider?>.IsAny))
            .Returns(new TryRail((string _, RailEnvironment environment, out IValueRailProvider? rail) =>
            {
                rail = environment == RailEnvironment.ForChain(ChainNetwork.Devnet) ? provider.Object : null;
                return rail is not null;
            }));
        factory.Setup(item => item.TryGetModule(provider.Object, out It.Ref<IDexTransactionModule?>.IsAny))
            .Returns(new TryModule((IBlockchainProvider _, out IDexTransactionModule? found) =>
            {
                found = declared ? module.Object : null;
                return declared;
            }));

        var views = await new DexModuleResolver(factory.Object).DescribeAsync();

        if (!declared)
        {
            views.Should().BeEmpty("an undeclared provider module is not advertised");
            return;
        }
        var view = views.Should().ContainSingle().Subject;
        view.Capability.Provider.Should().Be("UniswapV3");
        view.Ready.Should().Be(identityAvailable);
        view.Network.ProtocolIdentity.Should().Be(identityAvailable ? "eip155:31337" : "");
        if (!identityAvailable) view.Detail.Should().Be("node unreachable");
    }

    [Fact]
    public async Task Repeated_discovery_within_the_cache_window_reuses_one_node_round_trip()
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(item => item.ChainType).Returns("Ethereum");
        var module = new Mock<IDexTransactionModule>();
        module.SetupGet(item => item.ChainType).Returns("Ethereum");
        module.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Devnet);
        module.SetupGet(item => item.DexCapability).Returns(new DexCapability("UniswapV3", "classic-exact-input/1", true, false, true, true, true, []));
        module.Setup(item => item.DescribeDexNetworkAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<DexNetwork>.Success(new("Ethereum", "Devnet", "eip155:31337")));
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(item => item.GetAllEnabledProviders()).Returns([provider.Object]);
        factory.Setup(item => item.TryGetRailProvider("Ethereum", It.IsAny<RailEnvironment>(), out It.Ref<IValueRailProvider?>.IsAny))
            .Returns(new TryRail((string _, RailEnvironment environment, out IValueRailProvider? rail) =>
            {
                rail = environment == RailEnvironment.ForChain(ChainNetwork.Devnet) ? provider.Object : null;
                return rail is not null;
            }));
        factory.Setup(item => item.TryGetModule(provider.Object, out It.Ref<IDexTransactionModule?>.IsAny))
            .Returns(new TryModule((IBlockchainProvider _, out IDexTransactionModule? found) => { found = module.Object; return true; }));
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });

        var first = await new DexModuleResolver(factory.Object, cache).DescribeAsync();
        var second = await new DexModuleResolver(factory.Object, cache).DescribeAsync();

        second.Should().BeSameAs(first, "a scoped resolver per request still shares the bounded process cache");
        module.Verify(item => item.DescribeDexNetworkAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
