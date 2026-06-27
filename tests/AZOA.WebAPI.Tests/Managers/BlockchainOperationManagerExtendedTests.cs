using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Managers;

public class BlockchainOperationManagerExtendedTests
{
    private readonly Mock<IBlockchainOperationStore> _store;
    private readonly Mock<IBlockchainProvider> _algoProvider;
    private readonly Mock<IBlockchainProvider> _solProvider;
    private readonly BlockchainProviderFactory _chainFactory;
    private readonly FakeIdempotencyStore _idempotency;
    private readonly BlockchainOperationManager _manager;

    public BlockchainOperationManagerExtendedTests()
    {
        _store = new Mock<IBlockchainOperationStore>();

        _algoProvider = new Mock<IBlockchainProvider>();
        _algoProvider.Setup(p => p.ChainType).Returns("Algorand");
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { Result = "algo_tx" });
        _algoProvider.Setup(p => p.BurnAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { Result = "algo_burn" });
        _algoProvider.Setup(p => p.ExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { Result = "algo_ex" });
        _algoProvider.Setup(p => p.SwapAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { Result = "algo_swap" });
        _algoProvider.Setup(p => p.TransferAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { Result = "algo_xfer" });
        _algoProvider.Setup(p => p.DeployContractAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { Result = "algo_deploy" });
        _algoProvider.Setup(p => p.CallContractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<object> { Result = new object() });

        _solProvider = new Mock<IBlockchainProvider>();
        _solProvider.Setup(p => p.ChainType).Returns("Solana");

        var config = new ConfigurationBuilder().Build();
        _chainFactory = new BlockchainProviderFactory(new[] { _algoProvider.Object, _solProvider.Object }, config);
        _idempotency = new FakeIdempotencyStore();
        _manager = new BlockchainOperationManager(_store.Object, _chainFactory, _idempotency);
    }

    [Fact]
    public async Task ExecuteAsync_Burn_ShouldDelegateAndSetStatus()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Burn",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand", ["TokenId"] = "123", ["Amount"] = "5", ["WalletAddress"] = "addr" }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Burned);
        _algoProvider.Verify(p => p.BurnAsync("123", 5UL, "addr", It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Swap_ShouldDelegateAndSetStatus()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Swap",
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = "Algorand",
                ["TokenIn"] = "A",
                ["TokenOut"] = "B",
                ["AmountIn"] = "10.5",
                ["MinAmountOut"] = "9.5",
                ["WalletAddress"] = "addr"
            }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Swapped);
        _algoProvider.Verify(p => p.SwapAsync("A", "B", 10.5m, 9.5m, "addr", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Transfer_ShouldDelegateAndSetStatus()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Transfer",
            SourceHolonId = Guid.NewGuid(),
            RecipientAddress = "toAddr",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand", ["WalletAddress"] = "fromAddr" }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Transferred);
    }

    [Fact]
    public async Task ExecuteAsync_DeployContract_ShouldDelegateAndSetStatus()
    {
        var op = new BlockchainOperation
        {
            OperationType = "DeployContract",
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = "Algorand",
                ["ContractCode"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                ["WalletAddress"] = "addr"
            }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Deployed);
    }

    [Fact]
    public async Task ExecuteAsync_CallContract_ShouldDelegateAndSetStatus()
    {
        var op = new BlockchainOperation
        {
            OperationType = "CallContract",
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = "Algorand",
                ["ContractAddress"] = "0x123",
                ["Method"] = "mint",
                ["Args"] = "{}",
                ["WalletAddress"] = "addr"
            }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Called);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_ShouldSetUnknownStatus()
    {
        var op = new BlockchainOperation { OperationType = "Invalid" };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Unknown);
    }

    [Fact]
    public async Task ExecuteAsync_Composite_ShouldDoNothing()
    {
        var op = new BlockchainOperation { OperationType = "Composite" };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_WithProviderFailure_ShouldSetFailedStatus()
    {
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { IsError = true, Message = "Insufficient funds" });

        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Failed);
        op.Parameters.Should().ContainKey("Error");
    }

    [Fact]
    public async Task ExecuteAsync_WithSaveError_ShouldReturnError()
    {
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = true, Message = "DB Error" });

        var result = await _manager.ExecuteAsync(new BlockchainOperation { OperationType = "Mint" });

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithException_ShouldSetFailedStatus()
    {
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new InvalidOperationException("boom"));

        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.Failed);
        op.Parameters["Error"].Should().Be("boom");
    }

    [Fact]
    public async Task ExecuteAsync_UsesDefaultChain_WhenNotSpecified()
    {
        var op = new BlockchainOperation { OperationType = "Mint", TokenUri = "uri", Amount = 1, AssetType = "NFT" };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        await _manager.ExecuteAsync(op);

        _algoProvider.Verify(p => p.MintAsync("uri", 1UL, "NFT", It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UsesSpecifiedNetwork()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand", ["ChainNetwork"] = "Mainnet" }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        await _manager.ExecuteAsync(op);

        _algoProvider.Verify(p => p.Initialize(It.IsAny<BlockchainNetworkConfig>(), ChainNetwork.Mainnet), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidNetwork_ShouldFallbackToDevnet()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand", ["ChainNetwork"] = "Invalid" }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        await _manager.ExecuteAsync(op);

        _algoProvider.Verify(p => p.Initialize(It.IsAny<BlockchainNetworkConfig>(), ChainNetwork.Devnet), Times.Once);
    }
}
