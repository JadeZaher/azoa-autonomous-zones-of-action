using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests : IAsyncLifetime
{
    private readonly string _namespace = "test" + Guid.NewGuid().ToString("N");
    private readonly Guid _actor = Guid.NewGuid();
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealHoldCommandStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "hold_ledger", "hold_command", "hold_command_receipt",
            "platform_reserve_ledger", "consent_grant", "wallet", "hold_command_authorization", "hold_recovery_receipt", "hold_release_mandate", "hold_release_mandate_offer", "hold_mandate_use_receipt",
            "action", "action_node", "action_run", "action_node_execution", "exchange_hold_source", "api_key",
            "exchange_hold_settlement_receipt", "project_treasury_ledger", "exchange_purchase_allocation", "operation_log",
            "exchange_retired_resolution");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor, new ReserveOptions());
        await Save(new User { Id = _actor.ToString("N"), Username = _actor.ToString("N"),
            Email = _actor + "@test.invalid", PasswordHash = "test", IsActive = true });
        await Save(new PlatformReserveLedger { Id = ReserveId, Chain = "Algorand", Network = "Devnet",
            ProtocolIdentity = "genesis", AssetId = "42", ReserveAddress = "reserve", UnitName = "UNIT", TotalSupply = "1000",
            TenantId = "user:" + _actor.ToString("N"),
            Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
                { ["contributors"] = ReservePartitionCounters.Declaring(1000) }) });
    }

    public async Task DisposeAsync()
    {
        try { await SurrealTestSchema.DropAsync(_namespace); }
        finally { _connection?.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentOpenHasOneLiabilityAndImmutableReplayAfterRefundAndRevocation()
    {
        var intent = Open();
        var authority = Authority();
        var results = await Task.WhenAll(_store.CommitLocalAsync(intent, authority), _store.CommitLocalAsync(intent, authority));
        results.Should().OnlyContain(row => row != null);
        results[0]!.OutcomeJson.Should().Be(results[1]!.OutcomeJson);
        (await Counters()).Held.Should().Be(400);
        (await _executor.QueryAsync<HoldLedger>(SurrealQuery<HoldLedger>.From())).Should().ContainSingle();
        var refund = intent with { RequestKey = "refund", Kind = "Refund", Definition = null };
        (await _store.CommitLocalAsync(refund, authority)).Should().NotBeNull();
        (await Counters()).Held.Should().Be(0);
        (await Counters()).Available.Should().Be(1000);
        await Execute(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true).Set(row => row.IsActive, false).Build());
        (await _store.CommitLocalAsync(intent, authority))!.OutcomeJson.Should().Be(results[0]!.OutcomeJson);
        var original = JsonSerializer.Deserialize<HoldCommandView>(results[0]!.OutcomeJson)!;
        original.Kind.Should().Be("Open");
        original.State.Should().Be("Completed");
        (await _store.GetHoldAsync(original.HoldRecordId))!.State.Should().Be("Refunded");
    }

    [Fact]
    public async Task RefundAndChainRaceCannotReturnAndInheritTheSameCommitment()
    {
        var intent = Open();
        var authority = Authority();
        (await _store.CommitLocalAsync(intent, authority)).Should().NotBeNull();
        var refund = intent with { RequestKey = "refund", Kind = "Refund", Definition = null };
        var chain = intent with { RequestKey = "chain", Kind = "ReleaseIntoHold", Definition = null,
            NextHoldId = "child", NextBeneficiaryUserId = _actor,
            NextReleaseNotBefore = intent.Definition!.ReleaseNotBefore };
        var outcomes = await Task.WhenAll(_store.CommitLocalAsync(refund, authority), _store.CommitLocalAsync(chain, authority));
        outcomes.Count(row => row != null).Should().Be(1);
        var parent = (await _store.GetHoldAsync(HoldCommandIdentity.Hold(intent)))!;
        var counters = await Counters();
        if (parent.State == "Refunded")
        {
            counters.Held.Should().Be(0);
            counters.Available.Should().Be(1000);
            (await _store.GetHoldAsync(HoldCommandIdentity.Hold(intent, "child"))).Should().BeNull();
        }
        else
        {
            parent.State.Should().Be("ReleasedIntoHold");
            counters.Held.Should().Be(400);
            var childRefund = refund with { RequestKey = "child-refund", HoldId = "child" };
            (await _store.CommitLocalAsync(childRefund, authority)).Should().NotBeNull();
            (await Counters()).Available.Should().Be(1000);
            (await _store.CommitLocalAsync(refund, authority)).Should().BeNull();
        }
    }

    [Fact]
    public async Task ChangedIntentWrongOwnerAndRevokedAuthorityCannotCreateOrSpend()
    {
        var intent = Open();
        var authority = Authority();
        (await _store.CommitLocalAsync(intent, authority)).Should().NotBeNull();
        (await _store.CommitLocalAsync(intent with { Definition = intent.Definition! with { QuantityBaseUnits = "500" } }, authority))
            .Should().BeNull();
        var other = intent with { RequestKey = "another", HoldId = "another", OwnerUserId = Guid.NewGuid() };
        (await _store.CommitLocalAsync(other, authority)).Should().BeNull();
        await Execute(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true)
            .Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build());
        (await _store.CommitLocalAsync(intent with { RequestKey = "revoked", HoldId = "revoked" }, authority)).Should().BeNull();
        (await Counters()).Held.Should().Be(400);
    }

    private HoldCommandIntent Open()
    {
        var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Devnet, "genesis", "42", "reserve");
        return new(1, "open", "Open", "parent", _actor, _actor, null, null, asset,
            new(asset, "400", "Reserve", _actor, null, "contributors", DateTimeOffset.UtcNow.AddDays(1), false, "work"));
    }

    private HoldCommandAuthority Authority() => new(1, Direct: new(1,
        new(1, "Jwt", _actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
            null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin), [AzoaScopes.TransferSign], []));

    private static string ReserveId => PlatformReserveLedger.RecordIdFor("Algorand", "Devnet", "42");
    private async Task<ReservePartitionCounters> Counters()
    {
        var row = await _executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(ReserveId));
        return ReservePartitionCodec.Decode(row!.Partitions, out _)!["contributors"];
    }

    private async Task Save<T>(T row) where T : class, ISurrealRecord, new() => await Execute(SurrealWriter.Create(row));
    private async Task Execute(SurrealQuery query) => (await _executor.ExecuteAsync(query)).EnsureAllOk();
}
