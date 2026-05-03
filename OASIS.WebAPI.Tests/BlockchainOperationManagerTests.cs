using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests;

public class BlockchainOperationManagerTests
{
    private readonly Mock<IOASISStorageProvider> _provider;
    private readonly Mock<IBlockchainProvider> _algoProvider;
    private readonly Mock<IBlockchainProvider> _solProvider;
    private readonly ProviderContext _providerContext;
    private readonly BlockchainOperationManager _manager;

    public BlockchainOperationManagerTests()
    {
        _provider = new Mock<IOASISStorageProvider>();
        _provider.Setup(p => p.ProviderName).Returns("InMemory");

        _algoProvider = new Mock<IBlockchainProvider>();
        _algoProvider.Setup(p => p.ChainType).Returns("Algorand");
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new OASISResult<string> { Result = "algo_tx_123" });

        _solProvider = new Mock<IBlockchainProvider>();
        _solProvider.Setup(p => p.ChainType).Returns("Solana");
        _solProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new OASISResult<string> { Result = "sol_tx_456" });

        var config = new ConfigurationBuilder().Build();
        _providerContext = new ProviderContext(new[] { _provider.Object }, config, null);
        var factory = new BlockchainProviderFactory(new[] { _algoProvider.Object, _solProvider.Object }, config);
        _manager = new BlockchainOperationManager(_providerContext, factory);
    }

    [Fact]
    public async Task ExecuteAsync_Mint_ShouldDelegateToChainProvider()
    {
        var operation = new BlockchainOperation
        {
            OperationType = "Mint",
            TokenUri = "ipfs://test",
            Amount = 1,
            AssetType = "NFT",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" }
        };

        _provider.Setup(p => p.SaveBlockchainOperationAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new OASISResult<IBlockchainOperation> { Result = op });

        var result = await _manager.ExecuteAsync(operation);

        result.IsError.Should().BeFalse();
        operation.Status.Should().Be("Minted");
        operation.Parameters.Should().ContainKey("TxHash");
        _algoProvider.Verify(p => p.MintAsync("ipfs://test", 1, "NFT", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithChainFailure_ShouldSetFailedStatus()
    {
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new OASISResult<string> { IsError = true, Message = "Insufficient funds" });

        var operation = new BlockchainOperation
        {
            OperationType = "Mint",
            Amount = 1,
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" }
        };

        _provider.Setup(p => p.SaveBlockchainOperationAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new OASISResult<IBlockchainOperation> { Result = op });

        var result = await _manager.ExecuteAsync(operation);

        result.IsError.Should().BeFalse();
        operation.Status.Should().Be("Failed");
        operation.Parameters.Should().ContainKey("Error");
    }

    [Fact]
    public async Task BuildAndExecuteAsync_ShouldUseBuilderPattern()
    {
        _provider.Setup(p => p.SaveBlockchainOperationAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new OASISResult<IBlockchainOperation> { Result = op });

        var result = await _manager.BuildAndExecuteAsync(builder =>
            builder.ForAvatar(Guid.NewGuid())
                   .Mint("ipfs://test", 1, "NFT")
                   .Build());

        result.IsError.Should().BeFalse();
        result.Result!.OperationType.Should().Be("Mint");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnOperation()
    {
        var operation = new BlockchainOperation { Id = Guid.NewGuid(), Status = "Pending" };
        _provider.Setup(p => p.LoadBlockchainOperationAsync(operation.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IBlockchainOperation> { Result = operation });

        var result = await _manager.GetAsync(operation.Id);

        result.Result.Should().NotBeNull();
        result.Result!.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task GetByAvatarAsync_ShouldFilterByAvatarId()
    {
        var avatarId = Guid.NewGuid();
        _provider.Setup(p => p.LoadBlockchainOperationsByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IBlockchainOperation>>
                 {
                     Result = new[] { new BlockchainOperation { AvatarId = avatarId } }
                 });

        var result = await _manager.GetByAvatarAsync(avatarId);

        result.Result.Should().HaveCount(1);
    }
}
