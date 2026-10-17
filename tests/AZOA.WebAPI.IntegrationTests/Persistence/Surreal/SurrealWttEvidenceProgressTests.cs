using AZOA.WebAPI.Providers.Stores.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealWttEvidenceProgressTests : IAsyncLifetime
{
    private readonly string space = $"test{Guid.NewGuid():N}";
    private ISurrealConnection connection = null!;
    private SurrealWttEvidenceProgressStore store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(space, "wtt_evidence_progress");
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
    public async Task Competing_page_results_commit_one_cursor_and_exact_lost_response_replays_progress()
    {
        const string binding = "{\"application\":7,\"round\":500}";
        var claimed = await Task.WhenAll(store.ClaimAsync(binding, "{}"), store.ClaimAsync(binding, "{}"));
        Assert.Equal(claimed[0].Id, claimed[1].Id);
        var updates = await Task.WhenAll(store.AdvanceAsync(claimed[0], "{\"page\":1}", new string('a', 64), "A"),
            store.AdvanceAsync(claimed[1], "{\"page\":2}", new string('b', 64), "B"));
        var winner = Assert.Single(updates.Where(value => value is not null))!;
        var resumed = await store.ClaimAsync(binding, "{}");
        Assert.Equal(winner.StateJson, resumed.StateJson);
        Assert.Equal(winner.NextCursor, resumed.NextCursor);
        Assert.Equal(winner.Digest, resumed.Digest);
        Assert.Null(await store.AdvanceAsync(claimed[0], "{\"page\":3}", new string('c', 64), "C"));
        var completed = await store.AdvanceAsync(resumed, "{\"done\":true}", new string('d', 64), null);
        Assert.True(completed!.Complete); Assert.Null(completed.NextCursor);
        var replay = await store.ClaimAsync(binding, "{}");
        Assert.True(replay.Complete); Assert.Equal(2, replay.Pages); Assert.Null(replay.NextCursor);
    }

    [Fact]
    public async Task Changed_initial_semantics_conflict_and_different_boundary_has_separate_progress()
    {
        var first = await store.ClaimAsync("{\"round\":500}", "{\"version\":1}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimAsync("{\"round\":500}", "{\"version\":2}"));
        var next = await store.ClaimAsync("{\"round\":501}", "{\"version\":1}");
        Assert.NotEqual(first.Id, next.Id);
    }
}
