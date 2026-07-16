using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class NodeFeeScheduleManagerTests
{
    [Fact]
    public async Task GetScheduleAsync_WithNoPersistedRow_ReturnsZeroFeeVersionZero()
    {
        var manager = new NodeFeeScheduleManager(new FakeNodeFeeScheduleStore());

        var result = await manager.GetScheduleAsync();

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Version.Should().Be(0);
        result.Result.Mint.FlatBaseUnits.Should().Be("0");
        result.Result.Mint.Bps.Should().Be(0);
    }

    [Fact]
    public async Task UpdateScheduleAsync_PartialUpdatePreservesValues_IncrementsVersion_AndAudits()
    {
        var actor = Guid.NewGuid();
        var store = new FakeNodeFeeScheduleStore(new NodeFeeSchedule
        {
            Id = NodeFeeSchedule.LocalId,
            MintFlatBaseUnits = "5",
            MintBps = 25,
            TransferFlatBaseUnits = "7",
            TransferBps = 50,
            Version = 3,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        var manager = new NodeFeeScheduleManager(store);

        var result = await manager.UpdateScheduleAsync(new NodeFeeScheduleUpdateRequest
        {
            Mint = new NodeFeeScheduleEntryRequest { Bps = 100 },
        }, actor);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Version.Should().Be(4);
        result.Result.Mint.FlatBaseUnits.Should().Be("5");
        result.Result.Mint.Bps.Should().Be(100);
        result.Result.Transfer.FlatBaseUnits.Should().Be("7");
        result.Result.Transfer.Bps.Should().Be(50);
        result.Result.UpdatedByAvatarId.Should().Be(actor);
        store.Audits.Should().ContainSingle();
        store.Audits[0].PreviousVersion.Should().Be(3);
        store.Audits[0].NewVersion.Should().Be(4);
        store.Audits[0].PreviousScheduleJson.Should().NotBeNullOrWhiteSpace();
        store.Audits[0].ScheduleJson.Should().Contain("\"version\":4");
    }

    [Theory]
    [InlineData("-1", 0, "non-negative integer")]
    [InlineData("1.5", 0, "non-negative integer")]
    [InlineData("18446744073709551616", 0, "unsigned 64-bit")]
    [InlineData("0", -1, "between 0 and 10000")]
    [InlineData("0", 10001, "between 0 and 10000")]
    public async Task UpdateScheduleAsync_InvalidEntry_RejectsBeforeWrite(
        string flat,
        long bps,
        string expectedMessage)
    {
        var store = new FakeNodeFeeScheduleStore();
        var manager = new NodeFeeScheduleManager(store);

        var result = await manager.UpdateScheduleAsync(new NodeFeeScheduleUpdateRequest
        {
            Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = flat, Bps = bps },
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain(expectedMessage);
        store.SavedSchedule.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateScheduleAsync_WithStoreReadFailure_RejectsFailClosed()
    {
        var manager = new NodeFeeScheduleManager(
            new FakeNodeFeeScheduleStore(error: "surreal unavailable"));

        var result = await manager.UpdateScheduleAsync(
            new NodeFeeScheduleUpdateRequest(),
            Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("schedule unavailable");
    }

    [Fact]
    public async Task QuoteAsync_CombinesFlatAndBasisPoints_AndPinsVersion()
    {
        var manager = new NodeFeeScheduleManager(new FakeNodeFeeScheduleStore(new NodeFeeSchedule
        {
            Id = NodeFeeSchedule.LocalId,
            MintFlatBaseUnits = "5",
            MintBps = 250,
            Version = 9,
        }));

        var result = await manager.QuoteAsync(NodeFeeOperation.Mint, 1_000);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.GrossAmount.Should().Be("1000");
        result.Result.FeeAmount.Should().Be("30");
        result.Result.NetAmount.Should().Be("970");
        result.Result.ScheduleVersion.Should().Be(9);
    }

    [Fact]
    public async Task QuoteAsync_FeeAtLeastGross_RejectsFailClosed()
    {
        var manager = new NodeFeeScheduleManager(new FakeNodeFeeScheduleStore(new NodeFeeSchedule
        {
            Id = NodeFeeSchedule.LocalId,
            TransferFlatBaseUnits = "100",
            Version = 1,
        }));

        var result = await manager.QuoteAsync(NodeFeeOperation.Transfer, 100);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("must be less than the gross amount");
    }

    [Fact]
    public async Task QuoteAsync_UnknownOperation_RejectsFailClosed()
    {
        var manager = new NodeFeeScheduleManager(new FakeNodeFeeScheduleStore());

        var result = await manager.QuoteAsync((NodeFeeOperation)999, 100);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Node fee operation is not supported.");
    }

    [Fact]
    public async Task UpdateScheduleAsync_IdenticalRetry_ReturnsCurrentWithoutNewAudit()
    {
        var store = new FakeNodeFeeScheduleStore(new NodeFeeSchedule
        {
            Id = NodeFeeSchedule.LocalId,
            MintFlatBaseUnits = "5",
            MintBps = 100,
            Version = 4,
        });
        var manager = new NodeFeeScheduleManager(store);

        var result = await manager.UpdateScheduleAsync(new NodeFeeScheduleUpdateRequest
        {
            ExpectedVersion = 3,
            Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = "5", Bps = 100 },
        }, Guid.NewGuid());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Version.Should().Be(4);
        store.SavedSchedule.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateScheduleAsync_StaleVersionWithDifferentValues_RejectsConflict()
    {
        var store = new FakeNodeFeeScheduleStore(new NodeFeeSchedule
        {
            Id = NodeFeeSchedule.LocalId,
            MintFlatBaseUnits = "5",
            Version = 4,
        });
        var manager = new NodeFeeScheduleManager(store);

        var result = await manager.UpdateScheduleAsync(new NodeFeeScheduleUpdateRequest
        {
            ExpectedVersion = 3,
            Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = "6" },
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("version conflict");
        store.SavedSchedule.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAuditAsync_ClampsLimit()
    {
        var store = new FakeNodeFeeScheduleStore();
        var manager = new NodeFeeScheduleManager(store);

        var result = await manager.ListAuditAsync(10_000);

        result.IsError.Should().BeFalse(result.Message);
        store.LastAuditLimit.Should().Be(100);
    }

    private sealed class FakeNodeFeeScheduleStore : INodeFeeScheduleStore
    {
        private readonly NodeFeeSchedule? _initialSchedule;
        private readonly string? _error;

        public FakeNodeFeeScheduleStore(NodeFeeSchedule? initialSchedule = null, string? error = null)
        {
            _initialSchedule = initialSchedule;
            _error = error;
        }

        public NodeFeeSchedule? SavedSchedule { get; private set; }

        public List<NodeFeeAudit> Audits { get; } = new();

        public int? LastAuditLimit { get; private set; }

        public Task<AZOAResult<NodeFeeSchedule?>> GetScheduleAsync(CancellationToken ct = default)
            => Task.FromResult(_error is not null
                ? new AZOAResult<NodeFeeSchedule?> { IsError = true, Message = _error }
                : new AZOAResult<NodeFeeSchedule?>
                {
                    Result = SavedSchedule ?? _initialSchedule,
                    Message = "Success",
                });

        public Task<AZOAResult<NodeFeeSchedule>> UpdateScheduleWithAuditAsync(
            NodeFeeSchedule schedule,
            NodeFeeAudit audit,
            long? expectedVersion,
            CancellationToken ct = default)
        {
            SavedSchedule = schedule;
            Audits.Add(audit);
            return Task.FromResult(new AZOAResult<NodeFeeSchedule>
            {
                Result = schedule,
                Message = "Saved.",
            });
        }

        public Task<AZOAResult<IEnumerable<NodeFeeAudit>>> ListAuditAsync(
            int limit,
            CancellationToken ct = default)
        {
            LastAuditLimit = limit;
            return Task.FromResult(new AZOAResult<IEnumerable<NodeFeeAudit>>
            {
                Result = Audits,
                Message = "Success",
            });
        }
    }
}
