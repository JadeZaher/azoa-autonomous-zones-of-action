using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Connection;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealWttPreparationStoreTests : IAsyncLifetime
{
    private readonly string space = $"test{Guid.NewGuid():N}";
    private ISurrealConnection connection = null!;
    private SurrealWttPreparationStore store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(space, "wtt_preparation");
        connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = space, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        store = new(new DefaultSurrealExecutor(connection));
    }

    public async Task DisposeAsync()
    {
        connection?.Dispose();
        await SurrealTestSchema.DropAsync(space);
    }

    [Fact]
    public async Task ConcurrentPreparationAndLostResponseRetainWinningPlanAndOneSignedWire()
    {
        var binding = Binding();
        var winners = await Task.WhenAll(store.ClaimAsync(Candidate(binding, "blockhash-a")),
            store.ClaimAsync(Candidate(binding, "blockhash-b")));
        Assert.Equal(winners[0].PlanJson, winners[1].PlanJson);
        var saved = (await store.GetAsync(winners[0].Id))!;
        var filled = await Task.WhenAll(store.TrySaveSignedAsync(saved, "{\"wire\":\"a\"}"),
            store.TrySaveSignedAsync(saved, "{\"wire\":\"b\"}"));
        Assert.Single(filled.Where(value => value is not null));
        var replay = await store.ClaimAsync(Candidate(binding, "new-blockhash-after-restart"));
        Assert.Equal(saved.PlanJson, replay.PlanJson);
        Assert.Equal(filled.Single(value => value is not null)!.SignedPreparationJson, replay.SignedPreparationJson);
        Assert.Null(await store.TrySaveSignedAsync(saved, "{\"wire\":\"late\"}"));
    }

    [Fact]
    public async Task ChangedQuoteOrOwnerCannotCreateParallelCandidateForSameStageGeneration()
    {
        var binding = Binding();
        var saved = await store.ClaimAsync(Candidate(binding, "initial"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimAsync(Candidate(binding with { QuoteId = "other" }, "other")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimAsync(Candidate(binding with { ActorUserId = Guid.NewGuid() }, "other")));
        Assert.Equal(saved.BindingJson, (await store.GetAsync(saved.Id))!.BindingJson);
    }

    private static WttPreparationBinding Binding() => new("solana:local", Guid.NewGuid(), null,
        Guid.NewGuid().ToString("N"), "Source", "Transfer", 0, "intent", "quote");

    private static WttPreparation Candidate(WttPreparationBinding binding, string blockhash) => new()
    {
        Id = WttPreparationIdentity.Id(binding),
        BindingJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)),
        PlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { Blockhash = blockhash })),
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };
}
