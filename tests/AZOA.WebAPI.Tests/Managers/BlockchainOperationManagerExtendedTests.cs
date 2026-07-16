using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
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

        var config = BuildEnabledProviderConfig();
        _chainFactory = new BlockchainProviderFactory(
        [
            new BlockchainProviderRegistration("Algorand", () => _algoProvider.Object),
            new BlockchainProviderRegistration("Solana", () => _solProvider.Object),
        ], config);
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
    public async Task ExecuteAsync_WithUnexpectedProviderException_ShouldBubbleForCentralLogging()
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

        Func<Task> act = () => _manager.ExecuteAsync(op);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("boom");
        op.Status.Should().Be(OperationStatus.Unknown,
            "an exception without a durable transaction hash is terminally blocked from rebroadcast");
        op.Parameters["Error"].Should().NotContain("boom",
            "the persisted operator-facing error must not expose exception detail");
        var key = _idempotency.Keys.Single();
        (await _idempotency.GetAsync(key, CancellationToken.None))!.State
            .Should().Be(IdempotencyState.Failed,
                "unexpected execution must not strand the claim InProgress forever");
    }

    [Fact]
    public async Task ExecuteAsync_PostBroadcastPersistenceException_PersistsReconciliationState()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" }
        };
        var saveCount = 0;
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .Returns((IBlockchainOperation saved, CancellationToken _) =>
            {
                saveCount++;
                if (saveCount == 2)
                    throw new InvalidOperationException("post-broadcast save failed");
                return Task.FromResult(AZOAResult<IBlockchainOperation>.Success(saved));
            });

        Func<Task> act = () => _manager.ExecuteAsync(op);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("post-broadcast save failed");
        op.Status.Should().Be(OperationStatus.PendingConfirmation);
        op.Parameters["TxHash"].Should().Be("algo_tx");
        saveCount.Should().Be(3, "the outer exception boundary performs one recovery write");
        var key = _idempotency.Keys.Single();
        (await _idempotency.GetAsync(key, CancellationToken.None))!.State
            .Should().Be(IdempotencyState.InProgress,
                "a durable tx hash keeps the claim nonterminal for reconciliation");
    }

    [Fact]
    public async Task ExecuteAsync_PostBroadcastPersistenceErrorResult_PersistsReconciliationState()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" },
        };
        var saveCount = 0;
        _store.Setup(p => p.UpsertAsync(
                It.IsAny<IBlockchainOperation>(),
                It.IsAny<CancellationToken>()))
            .Returns((IBlockchainOperation saved, CancellationToken _) =>
            {
                saveCount++;
                return Task.FromResult(saveCount == 2
                    ? AZOAResult<IBlockchainOperation>.Failure("database unavailable", saved)
                    : AZOAResult<IBlockchainOperation>.Success(saved));
            });

        Func<Task> act = () => _manager.ExecuteAsync(op);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Post-broadcast operation persistence returned an error result.");
        op.Status.Should().Be(OperationStatus.PendingConfirmation);
        op.Parameters["TxHash"].Should().Be("algo_tx");
        var key = _idempotency.Keys.Single();
        (await _idempotency.GetAsync(key, CancellationToken.None))!.State
            .Should().Be(IdempotencyState.InProgress,
                "a recovered durable tx hash remains owned by reconciliation");
    }

    [Fact]
    public async Task ExecuteAsync_RecoveryPersistenceException_StillSettlesClaim()
    {
        _algoProvider.Setup(p => p.MintAsync(
                It.IsAny<string>(),
                It.IsAny<ulong>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<SigningContext?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider failed"));

        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" },
        };
        _store.SetupSequence(p => p.UpsertAsync(
                It.IsAny<IBlockchainOperation>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IBlockchainOperation>.Success(op))
            .ThrowsAsync(new IOException("recovery store unavailable"));

        Func<Task> act = () => _manager.ExecuteAsync(op);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("provider failed");
        var key = _idempotency.Keys.Single();
        (await _idempotency.GetAsync(key, CancellationToken.None))!.State
            .Should().Be(IdempotencyState.Failed,
                "claim recovery must run even when the operation recovery write fails");
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
    public async Task ExecuteAsync_WithInvalidNetwork_ShouldFailClosedBeforePersistenceOrProviderUse()
    {
        var op = new BlockchainOperation
        {
            OperationType = "Mint",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand", ["ChainNetwork"] = "Invalid" }
        };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation o, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = o });

        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid ChainNetwork");
        _store.Verify(
            p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _algoProvider.Verify(
            p => p.Initialize(It.IsAny<BlockchainNetworkConfig>(), It.IsAny<ChainNetwork>()),
            Times.Never);
    }

    private static IConfiguration BuildEnabledProviderConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = "Algorand",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Testnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Mainnet:IsEnabled"] = "true",
                ["Blockchain:Chains:1:ChainType"] = "Solana",
                ["Blockchain:Chains:1:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:1:Testnet:IsEnabled"] = "true",
                ["Blockchain:Chains:1:Mainnet:IsEnabled"] = "true",
            })
            .Build();
}
