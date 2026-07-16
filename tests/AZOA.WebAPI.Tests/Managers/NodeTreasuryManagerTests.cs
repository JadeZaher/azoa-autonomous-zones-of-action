using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class NodeTreasuryManagerTests
{
    [Fact]
    public async Task UpdateDestinationAsync_ValidProviderAndAddress_CreatesVersionedAuditedDestination()
    {
        var actor = Guid.NewGuid();
        var store = new FakeNodeTreasuryStore();
        var (manager, provider) = CreateManager(store);

        var result = await manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "algorand",
            Network = ChainNetwork.Testnet,
            Address = "  TREASURY-1  ",
            ExpectedVersion = 0,
        }, actor);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Chain.Should().Be("Algorand");
        result.Result.Network.Should().Be(ChainNetwork.Testnet);
        result.Result.Address.Should().Be("TREASURY-1");
        result.Result.Version.Should().Be(1);
        result.Result.UpdatedByAvatarId.Should().Be(actor);
        store.SavedDestination.Should().NotBeNull();
        store.ExpectedVersion.Should().BeNull();
        store.Audits.Should().ContainSingle();
        store.Audits[0].PreviousVersion.Should().Be(0);
        store.Audits[0].NewVersion.Should().Be(1);
        store.Audits[0].PreviousDestinationJson.Should().BeNull();
        store.Audits[0].DestinationJson.Should().Contain("\"address\":\"TREASURY-1\"");
        provider.Verify(
            p => p.ValidateAddressAsync("TREASURY-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateDestinationAsync_IdenticalRetryWithStaleVersion_ReturnsCurrentWithoutAudit()
    {
        var current = Destination("TREASURY-1", version: 4);
        var store = new FakeNodeTreasuryStore(current);
        var (manager, _) = CreateManager(store);

        var result = await manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "Algorand",
            Network = ChainNetwork.Testnet,
            Address = "TREASURY-1",
            ExpectedVersion = 3,
        }, Guid.NewGuid());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Version.Should().Be(4);
        result.Message.Should().Contain("No treasury destination changes");
        store.SavedDestination.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDestinationAsync_StaleVersionWithDifferentAddress_RejectsConflict()
    {
        var store = new FakeNodeTreasuryStore(Destination("TREASURY-1", version: 4));
        var (manager, _) = CreateManager(store);

        var result = await manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "Algorand",
            Network = ChainNetwork.Testnet,
            Address = "TREASURY-2",
            ExpectedVersion = 3,
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("version conflict");
        store.SavedDestination.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDestinationAsync_ConcurrentIdenticalWinner_ReturnsLatestAsIdempotentSuccess()
    {
        var before = Destination("OLD", version: 3);
        var winner = Destination("TREASURY-1", version: 4);
        var store = new Mock<INodeTreasuryStore>();
        store.SetupSequence(s => s.GetDestinationAsync(
                "Algorand",
                ChainNetwork.Testnet,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeTreasuryDestination?>
            {
                Result = before,
                Message = "Success",
            })
            .ReturnsAsync(new AZOAResult<NodeTreasuryDestination?>
            {
                Result = winner,
                Message = "Success",
            });
        store.Setup(s => s.UpdateDestinationWithAuditAsync(
                It.IsAny<NodeTreasuryDestination>(),
                It.IsAny<NodeTreasuryAudit>(),
                3,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeTreasuryDestination>
            {
                IsError = true,
                Message = "Node treasury destination version conflict",
            });
        var (manager, _) = CreateManager(store.Object);

        var result = await manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "Algorand",
            Network = ChainNetwork.Testnet,
            Address = "TREASURY-1",
            ExpectedVersion = 3,
        }, Guid.NewGuid());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Address.Should().Be("TREASURY-1");
        result.Result.Version.Should().Be(4);
        result.Message.Should().Contain("No treasury destination changes");
    }

    [Fact]
    public async Task UpdateDestinationAsync_UnconfiguredProvider_RejectsBeforeStoreAccess()
    {
        var store = new FakeNodeTreasuryStore();
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(f => f.GetProvider("Unknown", ChainNetwork.Mainnet))
            .Throws(new BlockchainProviderNotFoundException(
                "No provider registered for chain type: Unknown"));
        var manager = new NodeTreasuryManager(store, factory.Object);

        var result = await manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "Unknown",
            Network = ChainNetwork.Mainnet,
            Address = "address",
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("No configured blockchain provider");
        store.GetCalls.Should().Be(0);
        store.SavedDestination.Should().BeNull();
    }

    [Fact]
    public async Task UpdateDestinationAsync_InvalidProviderAddress_RejectsBeforeStoreAccess()
    {
        var store = new FakeNodeTreasuryStore();
        var (manager, provider) = CreateManager(
            store,
            new AZOAResult<bool> { Result = false, Message = "invalid address" });

        var result = await manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "Algorand",
            Network = ChainNetwork.Testnet,
            Address = "bad",
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("address validation failed");
        result.Message.Should().Contain("invalid address");
        store.GetCalls.Should().Be(0);
        provider.Verify(
            p => p.ValidateAddressAsync("bad", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateDestinationAsync_UnexpectedProviderException_BubblesForCentralLogging()
    {
        var store = new FakeNodeTreasuryStore();
        var (manager, provider) = CreateManager(store);
        provider.Setup(p => p.ValidateAddressAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider transport invariant failed"));

        Func<Task> act = () => manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "Algorand",
            Network = ChainNetwork.Testnet,
            Address = "TREASURY-1",
        }, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("provider transport invariant failed");
        store.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateDestinationAsync_StoreReadFailure_RejectsFailClosed()
    {
        var store = new FakeNodeTreasuryStore(error: "surreal unavailable");
        var (manager, _) = CreateManager(store);

        var result = await manager.UpdateDestinationAsync(new NodeTreasuryDestinationUpdateRequest
        {
            Chain = "Algorand",
            Network = ChainNetwork.Testnet,
            Address = "TREASURY-1",
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("destination unavailable");
        result.Message.Should().Contain("surreal unavailable");
        store.SavedDestination.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDestinationAsync_RevalidatesPersistedAddressFailClosed()
    {
        var store = new FakeNodeTreasuryStore(Destination("STALE", version: 2));
        var (manager, _) = CreateManager(
            store,
            new AZOAResult<bool> { IsError = true, Message = "account missing" });

        var result = await manager.GetDestinationAsync("Algorand", ChainNetwork.Testnet);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("address validation failed");
        result.Message.Should().Contain("account missing");
    }

    [Fact]
    public async Task ListAuditAsync_ClampsLimit()
    {
        var store = new FakeNodeTreasuryStore();
        var (manager, _) = CreateManager(store);

        var result = await manager.ListAuditAsync(10_000);

        result.IsError.Should().BeFalse(result.Message);
        store.LastAuditLimit.Should().Be(100);
    }

    private static (NodeTreasuryManager Manager, Mock<IBlockchainProvider> Provider) CreateManager(
        INodeTreasuryStore store,
        AZOAResult<bool>? validation = null)
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(p => p.ChainType).Returns("Algorand");
        provider.SetupGet(p => p.ActiveNetwork).Returns(ChainNetwork.Testnet);
        provider.Setup(p => p.ValidateAddressAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(validation ?? new AZOAResult<bool> { Result = true, Message = "valid" });

        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(f => f.GetProvider(
                It.Is<string>(chain => chain.Equals("Algorand", StringComparison.OrdinalIgnoreCase)),
                ChainNetwork.Testnet))
            .Returns(provider.Object);

        return (new NodeTreasuryManager(store, factory.Object), provider);
    }

    private static NodeTreasuryDestination Destination(string address, long version)
        => new()
        {
            Id = NodeTreasuryDestination.RecordIdFor("Algorand", "Testnet"),
            Chain = "Algorand",
            Network = "Testnet",
            Address = address,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class FakeNodeTreasuryStore : INodeTreasuryStore
    {
        private readonly NodeTreasuryDestination? _initialDestination;
        private readonly string? _error;

        public FakeNodeTreasuryStore(
            NodeTreasuryDestination? initialDestination = null,
            string? error = null)
        {
            _initialDestination = initialDestination;
            _error = error;
        }

        public int GetCalls { get; private set; }

        public NodeTreasuryDestination? SavedDestination { get; private set; }

        public long? ExpectedVersion { get; private set; }

        public List<NodeTreasuryAudit> Audits { get; } = new();

        public int? LastAuditLimit { get; private set; }

        public Task<AZOAResult<NodeTreasuryDestination?>> GetDestinationAsync(
            string chain,
            ChainNetwork network,
            CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(_error is not null
                ? new AZOAResult<NodeTreasuryDestination?> { IsError = true, Message = _error }
                : new AZOAResult<NodeTreasuryDestination?>
                {
                    Result = SavedDestination ?? _initialDestination,
                    Message = "Success",
                });
        }

        public Task<AZOAResult<NodeTreasuryDestination>> UpdateDestinationWithAuditAsync(
            NodeTreasuryDestination destination,
            NodeTreasuryAudit audit,
            long? expectedVersion,
            CancellationToken ct = default)
        {
            SavedDestination = destination;
            ExpectedVersion = expectedVersion;
            Audits.Add(audit);
            return Task.FromResult(new AZOAResult<NodeTreasuryDestination>
            {
                Result = destination,
                Message = "Saved.",
            });
        }

        public Task<AZOAResult<IEnumerable<NodeTreasuryAudit>>> ListAuditAsync(
            int limit,
            CancellationToken ct = default)
        {
            LastAuditLimit = limit;
            return Task.FromResult(new AZOAResult<IEnumerable<NodeTreasuryAudit>>
            {
                Result = Audits,
                Message = "Success",
            });
        }
    }
}
