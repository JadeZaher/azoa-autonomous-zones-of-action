using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealSwapFeePolicyTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "node_fee_schedule", "node_fee_audit");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _executor = new DefaultSurrealExecutor(_connection);
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task ConcurrentDifferentAdoptionsCommitOnePolicyAndMatchingAudit()
    {
        var actor = Guid.NewGuid();
        var store = new SurrealNodeFeeScheduleStore(_executor);
        var manager = new NodeFeeScheduleManager(store);
        var results = await Task.WhenAll(new[] { "3", "7" }.Select(flat => manager.UpdateScheduleAsync(new()
        {
            ExpectedVersion = 0,
            SwapFlatDenomination = NodeFeeSchedule.InputBaseUnitsV1,
            Swap = new() { FlatBaseUnits = flat, Bps = 25 },
        }, actor)));
        results.Where(result => !result.IsError).Should().ContainSingle();
        var saved = (await store.GetScheduleAsync()).Result!;
        saved.Version.Should().Be(1);
        saved.SwapFlatDenomination.Should().Be(NodeFeeSchedule.InputBaseUnitsV1);
        var audit = (await store.ListAuditAsync(20)).Result!.Should().ContainSingle().Which;
        audit.NewVersion.Should().Be(saved.Version);
        audit.ScheduleJson.Should().Contain($"\"flatBaseUnits\":\"{saved.SwapFlatBaseUnits}\"");
        audit.ScheduleJson.Should().Contain(NodeFeeSchedule.InputBaseUnitsV1);
        var reconstructed = new NodeFeeScheduleManager(new SurrealNodeFeeScheduleStore(_executor));
        var quote = await reconstructed.QuoteAsync(NodeFeeOperation.Swap, 10000);
        quote.IsError.Should().BeFalse(quote.Message);
        quote.Result!.FeeAmount.Should().Be((ulong.Parse(saved.SwapFlatBaseUnits) + 25).ToString());
    }

    [Fact]
    public async Task LegacyDenominationUpdateUsesVersionCasAndPreservesOtherPolicyFields()
    {
        (await _executor.ExecuteAsync(SurrealWriter.Create(new NodeFeeSchedule
        {
            Id = NodeFeeSchedule.LocalId, Version = 4, SwapFlatBaseUnits = "9", MintFlatBaseUnits = "2",
            UpdatedAt = DateTimeOffset.UtcNow,
        }))).EnsureAllOk();
        var store = new SurrealNodeFeeScheduleStore(_executor);
        var manager = new NodeFeeScheduleManager(store);
        (await manager.QuoteAsync(NodeFeeOperation.Swap, 1000)).IsError.Should().BeTrue();
        var request = new NodeFeeScheduleUpdateRequest
        {
            ExpectedVersion = 3, SwapFlatDenomination = NodeFeeSchedule.InputBaseUnitsV1,
        };
        (await manager.UpdateScheduleAsync(request, Guid.NewGuid())).IsError.Should().BeTrue();
        (await store.ListAuditAsync(20)).Result.Should().BeEmpty();
        request.ExpectedVersion = 4;
        (await manager.UpdateScheduleAsync(request, Guid.NewGuid())).IsError.Should().BeFalse();
        var saved = (await store.GetScheduleAsync()).Result!;
        saved.MintFlatBaseUnits.Should().Be("2");
        saved.SwapFlatBaseUnits.Should().Be("9");
        saved.Version.Should().Be(5);
        saved.SwapFlatDenomination.Should().Be(NodeFeeSchedule.InputBaseUnitsV1);
        (await store.ListAuditAsync(20)).Result.Should().ContainSingle();
    }
}
