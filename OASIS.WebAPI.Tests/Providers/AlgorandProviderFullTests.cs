using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Providers.Blockchain;

namespace OASIS.WebAPI.Tests.Providers;

public class AlgorandProviderFullTests
{
    private readonly AlgorandProvider _provider;

    public AlgorandProviderFullTests()
    {
        var config = new ConfigurationBuilder().Build();
        _provider = new AlgorandProvider(config);
    }

    [Fact]
    public void ChainType_ShouldBeAlgorand()
    {
        _provider.ChainType.Should().Be("Algorand");
    }

    [Fact]
    public void CapabilityName_ShouldBeAlgorandASA()
    {
        _provider.CapabilityName.Should().Be("Algorand.ASA");
    }

    [Fact]
    public void Initialize_ShouldSetActiveNetwork()
    {
        _provider.Initialize(new BlockchainNetworkConfig(), ChainNetwork.Testnet);
        _provider.ActiveNetwork.Should().Be(ChainNetwork.Testnet);
    }

    [Fact]
    public async Task GetBalanceAsync_ShouldReturnResult()
    {
        var result = await _provider.GetBalanceAsync("addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().Be("0");
        result.Message.Should().Be("Balance retrieved from Algorand.");
    }

    [Fact]
    public async Task ValidateAddressAsync_ShouldReturnTrue()
    {
        var result = await _provider.ValidateAddressAsync("addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Be("Address validated on Algorand.");
    }

    [Fact]
    public async Task MintAsync_ShouldReturnTxHash()
    {
        var result = await _provider.MintAsync("uri", 1, "NFT", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_tx_");
        result.Message.Should().Be("Minted 1 NFT on Algorand.");
    }

    [Fact]
    public async Task BurnAsync_ShouldReturnTxHash()
    {
        var result = await _provider.BurnAsync("token", 5, "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_tx_");
        result.Message.Should().Be("Burned 5 of asset token on Algorand.");
    }

    [Fact]
    public async Task TransferAsync_ShouldReturnTxHash()
    {
        var result = await _provider.TransferAsync("token", "from", "to", 1);
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_tx_");
        result.Message.Should().Be("Transferred asset token on Algorand.");
    }

    [Fact]
    public async Task ExchangeAsync_ShouldReturnTxHash()
    {
        var result = await _provider.ExchangeAsync("A", "B", "1:1", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_tx_");
        result.Message.Should().Be("Exchanged A for B on Algorand.");
    }

    [Fact]
    public async Task SwapAsync_ShouldReturnTxHash()
    {
        var result = await _provider.SwapAsync("A", "B", 10, 9, "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_tx_");
        result.Message.Should().Be("Swapped A for B on Algorand.");
    }

    [Fact]
    public async Task GetTokenMetadataAsync_ShouldReturnMetadata()
    {
        var result = await _provider.GetTokenMetadataAsync("123");
        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("chain");
        result.Result!["chain"].Should().Be("Algorand");
        result.Message.Should().Be("Metadata fetched from Algorand.");
    }

    [Fact]
    public async Task GetTokensByOwnerAsync_ShouldReturnEmptyList()
    {
        var result = await _provider.GetTokensByOwnerAsync("addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeEmpty();
        result.Message.Should().Be("Tokens retrieved from Algorand.");
    }

    [Fact]
    public async Task GetTransactionStatusAsync_ShouldReturnStatus()
    {
        var result = await _provider.GetTransactionStatusAsync("tx");
        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("status");
        result.Message.Should().Be("Transaction status retrieved from Algorand.");
    }

    [Fact]
    public async Task DeployContractAsync_ShouldReturnAppId()
    {
        var result = await _provider.DeployContractAsync(new byte[] { 1 }, "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_app_");
        result.Message.Should().Be("Contract deployed on Algorand.");
    }

    [Fact]
    public async Task CallContractAsync_ShouldReturnResult()
    {
        var result = await _provider.CallContractAsync("addr", "method", new(), "wallet");
        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Message.Should().Be("Called method method on Algorand contract.");
    }

    [Fact]
    public async Task GetChainInfoAsync_ShouldReturnInfo()
    {
        _provider.Initialize(new BlockchainNetworkConfig(), ChainNetwork.Devnet);
        var result = await _provider.GetChainInfoAsync();
        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("chain");
        result.Result!["chain"].Should().Be("Algorand");
        result.Message.Should().Be("Chain info retrieved from Algorand.");
    }

    [Fact]
    public async Task CreateASAAsync_ShouldReturnAssetId()
    {
        var module = (IAlgorandASAModule)_provider;
        var result = await module.CreateASAAsync("MyToken", "MTK", 1000, 2, "m", "r", "f", "c", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("algo_asa_");
        result.Message.Should().Be("Created ASA MyToken on Algorand.");
    }

    [Fact]
    public async Task OptInAsync_ShouldReturnTrue()
    {
        var module = (IAlgorandASAModule)_provider;
        var result = await module.OptInAsync("123", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Be("Opted in to asset 123 on Algorand.");
    }

    [Fact]
    public async Task GetAssetHoldingAsync_ShouldReturnBalance()
    {
        var module = (IAlgorandASAModule)_provider;
        var result = await module.GetAssetHoldingAsync("123", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().Be("0");
        result.Message.Should().Be("Asset holding retrieved for 123 on Algorand.");
    }
}
