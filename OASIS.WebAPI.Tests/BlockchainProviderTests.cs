using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OASIS.WebAPI.Providers.Blockchain;

namespace OASIS.WebAPI.Tests;

public class BlockchainProviderTests
{
    private readonly IConfiguration _config;

    public BlockchainProviderTests()
    {
        _config = new ConfigurationBuilder().Build();
    }

    [Fact]
    public void AlgorandProvider_ShouldReportCorrectChainType()
    {
        var provider = new AlgorandProvider(_config);
        provider.ChainType.Should().Be("Algorand");
    }

    [Fact]
    public async Task AlgorandProvider_MintAsync_ShouldReturnTxHash()
    {
        var provider = new AlgorandProvider(_config);
        var result = await provider.MintAsync("ipfs://test", 1, "NFT", "addr");

        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_tx_");
    }

    [Fact]
    public async Task AlgorandProvider_GetTokenMetadataAsync_ShouldReturnMetadata()
    {
        var provider = new AlgorandProvider(_config);
        var result = await provider.GetTokenMetadataAsync("123");

        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("chain");
        result.Result!["chain"].Should().Be("Algorand");
    }

    [Fact]
    public void SolanaProvider_ShouldReportCorrectChainType()
    {
        var provider = new SolanaProvider(_config);
        provider.ChainType.Should().Be("Solana");
    }

    [Fact]
    public async Task SolanaProvider_MintAsync_ShouldReturnTxHash()
    {
        var provider = new SolanaProvider(_config);
        var result = await provider.MintAsync("ipfs://test", 1, "NFT", "addr");

        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_tx_");
    }

    [Fact]
    public async Task SolanaProvider_GetTokenMetadataAsync_ShouldReturnMetadata()
    {
        var provider = new SolanaProvider(_config);
        var result = await provider.GetTokenMetadataAsync("mint123");

        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("chain");
        result.Result!["chain"].Should().Be("Solana");
    }
}
