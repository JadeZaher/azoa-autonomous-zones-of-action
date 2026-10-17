using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealNativePreparationStoreTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealNativePreparationStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "native_preparation");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _store = new SurrealNativePreparationStore(new DefaultSurrealExecutor(_connection));
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task ConcurrentPreparationClaimsKeepOnePlanBeforeSigning()
    {
        var first = Candidate();
        var second = Candidate();
        second.Id = first.Id;
        second.PlanJson = "{\"blockhash\":\"later-blockhash\"}";
        var claimed = await Task.WhenAll(_store.ClaimAsync(first), _store.ClaimAsync(second));
        claimed[0].PlanJson.Should().Be(claimed[1].PlanJson);
        claimed[0].PreparedJson.Should().BeNull();
        var recovered = (await _store.GetAsync(first.Id))!;
        recovered.PlanJson.Should().Be(claimed[0].PlanJson);
        recovered.IntentHash.Should().Be(first.IntentHash);
    }

    [Fact]
    public async Task LostPreparationResponseReusesCanonicalWireAndCannotReplaceIt()
    {
        var row = await _store.ClaimAsync(Candidate());
        const string wire = "{\"transactionId\":\"exact-wire\"}";
        await _store.SavePreparedAsync(row.Id, row.IntentHash, wire);
        var replay = await _store.SavePreparedAsync(row.Id, row.IntentHash, wire);
        replay.PreparedJson.Should().Be(wire);
        var overwrite = () => _store.SavePreparedAsync(row.Id, row.IntentHash, "{\"transactionId\":\"other-wire\"}");
        await overwrite.Should().ThrowAsync<InvalidOperationException>();
        (await _store.GetAsync(row.Id))!.PreparedJson.Should().Be(wire);
    }

    [Fact]
    public async Task ChangedIntentCannotFillAnUnpreparedPlan()
    {
        var row = await _store.ClaimAsync(Candidate());
        var overwrite = () => _store.SavePreparedAsync(row.Id, "changed-intent", "{}");
        await overwrite.Should().ThrowAsync<InvalidOperationException>();
        (await _store.GetAsync(row.Id))!.PreparedJson.Should().BeNull();
    }

    private static NativePreparation Candidate() => new()
    {
        Id = Guid.NewGuid().ToString("N"), IntentHash = "immutable-intent", Stage = "Execute", Generation = 0,
        PlanJson = "{\"blockhash\":\"original-blockhash\"}", CreatedAt = DateTimeOffset.UtcNow,
    };
}
