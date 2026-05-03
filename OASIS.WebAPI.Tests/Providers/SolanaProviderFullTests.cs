using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Providers.Blockchain;

namespace OASIS.WebAPI.Tests.Providers;

public class SolanaProviderFullTests
{
    private readonly SolanaProvider _provider;

    public SolanaProviderFullTests()
    {
        var config = new ConfigurationBuilder().Build();
        _provider = new SolanaProvider(config);
    }

    [Fact]
    public void ChainType_ShouldBeSolana()
    {
        _provider.ChainType.Should().Be("Solana");
    }

    [Fact]
    public void CapabilityName_ShouldBeSolanaMetaplex()
    {
        _provider.CapabilityName.Should().Be("Solana.Metaplex");
    }

    [Fact]
    public void Initialize_ShouldSetActiveNetworkAndReconfigureClient()
    {
        _provider.Initialize(new BlockchainNetworkConfig { NodeUrl = "https://api.mainnet.solana.com" }, ChainNetwork.Mainnet);
        _provider.ActiveNetwork.Should().Be(ChainNetwork.Mainnet);
    }

    [Fact]
    public async Task GetBalanceAsync_ShouldReturnResult()
    {
        var result = await _provider.GetBalanceAsync("addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().Be("0");
        result.Message.Should().Be("Retrieved balance for addr on Solana.");
    }

    [Fact]
    public async Task ValidateAddressAsync_ValidLength_ShouldReturnTrue()
    {
        var result = await _provider.ValidateAddressAsync("12345678901234567890123456789012");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Be("Address validation completed on Solana.");
    }

    [Fact]
    public async Task ValidateAddressAsync_TooShort_ShouldReturnFalse()
    {
        var result = await _provider.ValidateAddressAsync("short");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeFalse();
        result.Message.Should().Be("Address validation completed on Solana.");
    }

    [Fact]
    public async Task ValidateAddressAsync_Empty_ShouldReturnFalse()
    {
        var result = await _provider.ValidateAddressAsync("");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeFalse();
        result.Message.Should().Be("Address validation completed on Solana.");
    }

    [Fact]
    public async Task ValidateAddressAsync_MaxLength_ShouldReturnTrue()
    {
        var result = await _provider.ValidateAddressAsync(new string('1', 44));
        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAddressAsync_TooLong_ShouldReturnFalse()
    {
        var result = await _provider.ValidateAddressAsync(new string('1', 45));
        result.IsError.Should().BeFalse();
        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task MintAsync_ShouldReturnTxHash()
    {
        var result = await _provider.MintAsync("uri", 1, "NFT", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_tx_");
        result.Message.Should().Be("Minted 1 NFT on Solana.");
    }

    [Fact]
    public async Task BurnAsync_ShouldReturnTxHash()
    {
        var result = await _provider.BurnAsync("token", 5, "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_tx_");
        result.Message.Should().Be("Burned 5 of asset token on Solana.");
    }

    [Fact]
    public async Task TransferAsync_ShouldReturnTxHash()
    {
        var result = await _provider.TransferAsync("token", "from", "to", 1);
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_tx_");
        result.Message.Should().Be("Transferred asset token on Solana.");
    }

    [Fact]
    public async Task ExchangeAsync_ShouldReturnTxHash()
    {
        var result = await _provider.ExchangeAsync("A", "B", "1:1", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_tx_");
        result.Message.Should().Be("Exchanged A for B on Solana.");
    }

    [Fact]
    public async Task SwapAsync_ShouldReturnTxHash()
    {
        var result = await _provider.SwapAsync("A", "B", 10, 9, "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_tx_");
        result.Message.Should().Be("Swapped A for B on Solana.");
    }

    [Fact]
    public async Task GetTokenMetadataAsync_ShouldReturnMetadata()
    {
        var result = await _provider.GetTokenMetadataAsync("mint");
        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("chain");
        result.Result!["chain"].Should().Be("Solana");
        result.Message.Should().Be("Metadata fetched from Solana.");
    }

    [Fact]
    public async Task GetTokensByOwnerAsync_ShouldReturnEmptyList()
    {
        var result = await _provider.GetTokensByOwnerAsync("addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeEmpty();
        result.Message.Should().Be("Retrieved tokens for addr on Solana.");
    }

    [Fact]
    public async Task GetTransactionStatusAsync_ShouldReturnConfirmed()
    {
        var result = await _provider.GetTransactionStatusAsync("tx");
        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("status");
        result.Result!["status"].Should().Be("confirmed");
        result.Message.Should().Be("Transaction status retrieved from Solana.");
    }

    [Fact]
    public async Task DeployContractAsync_ShouldReturnProgramId()
    {
        var result = await _provider.DeployContractAsync(new byte[] { 1 }, "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_prog_");
        result.Message.Should().Be("Program deployed on Solana.");
    }

    [Fact]
    public async Task CallContractAsync_ShouldReturnResult()
    {
        var result = await _provider.CallContractAsync("addr", "method", new(), "wallet");
        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Message.Should().Be("Called method method on program addr on Solana.");
    }

    [Fact]
    public async Task GetChainInfoAsync_ShouldReturnInfo()
    {
        var result = await _provider.GetChainInfoAsync();
        result.IsError.Should().BeFalse();
        result.Result.Should().ContainKey("chain");
        result.Result!["chain"].Should().Be("Solana");
        result.Message.Should().Be("Solana chain info retrieved.");
    }

    [Fact]
    public async Task CreateMetadataAccountAsync_ShouldReturnAccount()
    {
        var module = (ISolanaMetaplexModule)_provider;
        var result = await module.CreateMetadataAccountAsync("mint", "Name", "SYM", "uri", 500, "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_meta_");
        result.Message.Should().Be("Created metadata account for mint mint on Solana.");
    }

    [Fact]
    public async Task UpdateMetadataAsync_ShouldReturnTrue()
    {
        var module = (ISolanaMetaplexModule)_provider;
        var result = await module.UpdateMetadataAsync("mint", "newUri", "newName", "addr");
        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Be("Updated metadata for mint mint on Solana.");
    }

    [Fact]
    public async Task CreateTokenAccountAsync_ShouldReturnAccount()
    {
        var module = (ISolanaSPLModule)_provider;
        var result = await module.CreateTokenAccountAsync("mint", "owner");
        result.IsError.Should().BeFalse();
        result.Result.Should().StartWith("sol_ta_");
        result.Message.Should().Be("Created token account for mint mint on Solana.");
    }

    [Fact]
    public async Task CloseTokenAccountAsync_ShouldReturnAccount()
    {
        var module = (ISolanaSPLModule)_provider;
        var result = await module.CloseTokenAccountAsync("account", "owner");
        result.IsError.Should().BeFalse();
        result.Result.Should().Be("account");
        result.Message.Should().Be("Closed token account account on Solana.");
    }
}
