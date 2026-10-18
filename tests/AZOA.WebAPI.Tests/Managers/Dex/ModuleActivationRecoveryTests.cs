using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Services.Dex;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>D-7 pins gate new admissions only (RV-L5-11), and fail closed on a deployed host that runs with development pins.</summary>
public sealed class ModuleActivationRecoveryTests
{
    private static readonly DexNetwork Devnet = new("Ethereum", "Devnet", "eip155:11155111");

    [Theory]
    [InlineData(true, null, null, true)]
    [InlineData(false, null, null, false)]
    [InlineData(true, "railway-project", null, false)]
    [InlineData(true, "railway-project", "true", true)]
    public void A_pin_admits_new_swaps_only_where_it_is_on_and_the_host_honours_it(bool pinned, string? railwayProject, string? acknowledged,
        bool admits)
    {
        var resolver = Resolver(new()
        {
            [ChainModuleActivation.Key(ChainModuleActivation.Dex, "Ethereum", ChainNetwork.Devnet)] = pinned ? "true" : null,
            ["RAILWAY_PROJECT_ID"] = railwayProject,
            [ChainModuleActivation.DeployedHostAcknowledgementKey] = acknowledged,
        });

        (resolver.Resolve(Devnet) is not null).Should().Be(admits);
        resolver.ResolveForExisting(Devnet).Should().NotBeNull("a swap admitted while the pin was on can always recover and clean up");
    }

    [Theory]
    [InlineData("https://eth-sepolia.example.quiknode.pro/3f9c1a0b7d2e4c6f8a1b/", "https://eth-sepolia.example.quiknode.pro")]
    [InlineData("https://user:secret@rpc.example:8545/v1?key=abc", "https://rpc.example:8545")]
    public void A_logged_rpc_endpoint_keeps_only_scheme_host_and_port(string configured, string logged)
        => ChainRpcHttp.LogSafeEndpoint(configured).Should().Be(logged);

    private static DexModuleResolver Resolver(Dictionary<string, string?> extra)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blockchain:DefaultChain"] = "Ethereum", ["Blockchain:DefaultNetwork"] = "Devnet",
            ["Blockchain:Chains:0:ChainType"] = "Ethereum", ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://node.test/", ["Blockchain:Chains:0:Devnet:ChainId"] = "11155111",
        }.Concat(extra).ToDictionary()).Build();
        var factory = new BlockchainProviderFactory(
            [new BlockchainProviderRegistration("Ethereum", () => new EthereumProvider(config, NullLogger<EthereumProvider>.Instance))], config);
        return new DexModuleResolver(factory);
    }
}
