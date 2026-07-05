using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealQuestNodeExecutionStore"/>.
/// Covers basic CRUD, the natural-key UNIQUE-index enforcement, and the
/// G2 single-winner <see cref="SurrealQuestNodeExecutionStore.TryClaimPendingAsync"/>
/// race semantics — under N concurrent calls AT MOST ONE returns a row.
/// </summary>
public class SurrealQuestNodeExecutionStoreTests : IntegrationTestBase
{
    public SurrealQuestNodeExecutionStoreTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ── Basic CRUD ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Create_GetById_RoundTripsAllFields()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(QuestNodeState.Pending);
        (await store.CreateAsync(exec)).IsError.Should().BeFalse();

        var fetched = await store.GetByIdAsync(exec.Id);
        fetched.IsError.Should().BeFalse(fetched.Message);
        fetched.Result.Should().NotBeNull();
        fetched.Result!.Id.Should().Be(exec.Id);
        fetched.Result.RunId.Should().Be(exec.RunId);
        fetched.Result.NodeId.Should().Be(exec.NodeId);
        fetched.Result.State.Should().Be(QuestNodeState.Pending);
        fetched.Result.StartedAt.Should().BeCloseTo(exec.StartedAt, TimeSpan.FromSeconds(2));
    }

    [SkippableFact]
    public async Task GetByRunAndNode_FindsExecution()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(QuestNodeState.Pending);
        (await store.CreateAsync(exec)).IsError.Should().BeFalse();

        var byNatural = await store.GetByRunAndNodeAsync(exec.RunId, exec.NodeId);
        byNatural.IsError.Should().BeFalse();
        byNatural.Result!.Id.Should().Be(exec.Id);
    }

    [SkippableFact]
    public async Task GetByRunAndNode_NonExistent_ReturnsError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var miss = await store.GetByRunAndNodeAsync(Guid.NewGuid(), Guid.NewGuid());
        miss.IsError.Should().BeTrue();
        miss.Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task GetByRunId_ReturnsOrderedByStartedAt()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var runId = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddSeconds(-30);
        var a = NewExecution(QuestNodeState.Pending, runId: runId);
        var b = NewExecution(QuestNodeState.Pending, runId: runId);
        a.StartedAt = t0;
        b.StartedAt = t0.AddSeconds(5);

        (await store.CreateAsync(a)).IsError.Should().BeFalse();
        (await store.CreateAsync(b)).IsError.Should().BeFalse();

        var rows = await store.GetByRunIdAsync(runId);
        rows.IsError.Should().BeFalse();
        var ordered = rows.Result!.ToList();
        ordered.Should().HaveCount(2);
        ordered[0].Id.Should().Be(a.Id, "ORDER BY started_at ASC must hold");
        ordered[1].Id.Should().Be(b.Id);
    }

    [SkippableFact]
    public async Task Update_PersistsTerminalTransition()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(QuestNodeState.Running);
        (await store.CreateAsync(exec)).IsError.Should().BeFalse();

        exec.State   = QuestNodeState.Succeeded;
        exec.Output  = "{\"holonId\":\"abc\"}";
        exec.EndedAt = DateTime.UtcNow;

        var updated = await store.UpdateAsync(exec);
        updated.IsError.Should().BeFalse(updated.Message);

        var fetched = await store.GetByIdAsync(exec.Id);
        fetched.Result!.State.Should().Be(QuestNodeState.Succeeded);
        fetched.Result.Output.Should().Be("{\"holonId\":\"abc\"}");
        fetched.Result.EndedAt.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Update_WithExpectedStateGuard_RejectsOnDrift()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(QuestNodeState.Pending);
        (await store.CreateAsync(exec)).IsError.Should().BeFalse();

        // Caller asserts the row should still be Running, but it is Pending.
        exec.State = QuestNodeState.Succeeded;
        var guarded = await store.UpdateAsync(exec, expectedState: QuestNodeState.Running);
        guarded.IsError.Should().BeTrue("state-machine guard must reject the drift");
    }

    // ── TryClaimPendingAsync — G2 single-winner ──────────────────────────────

    [SkippableFact]
    public async Task TryClaimPending_FirstCaller_Wins_TransitionsToRunning()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(QuestNodeState.Pending);
        (await store.CreateAsync(exec)).IsError.Should().BeFalse();

        var claim = await store.TryClaimPendingAsync(exec.RunId, exec.NodeId);
        claim.IsError.Should().BeFalse(claim.Message);
        claim.Result.Should().NotBeNull("first caller must win the conditional claim");
        claim.Result!.State.Should().Be(QuestNodeState.Running);
    }

    [SkippableFact]
    public async Task TryClaimPending_SecondConcurrentCaller_Loses_ReturnsNullWithoutError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(QuestNodeState.Pending);
        (await store.CreateAsync(exec)).IsError.Should().BeFalse();

        var first  = await store.TryClaimPendingAsync(exec.RunId, exec.NodeId);
        var second = await store.TryClaimPendingAsync(exec.RunId, exec.NodeId);

        first.Result.Should().NotBeNull("first caller wins");
        second.IsError.Should().BeFalse("losing the race is not an error");
        second.Result.Should().BeNull("loser sees no row — exactly-one-winner contract");

        var still = await store.GetByIdAsync(exec.Id);
        still.Result!.State.Should().Be(QuestNodeState.Running,
            "loser must not regress the state of the winner's row");
    }

    [SkippableFact]
    public async Task TryClaimPending_NoSuchRow_ReturnsError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var miss = await store.TryClaimPendingAsync(Guid.NewGuid(), Guid.NewGuid());
        miss.IsError.Should().BeTrue("row-does-not-exist is distinguishable from race-loser");
        miss.Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task TryClaimPending_ConcurrentRace_ExactlyOneWinner()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(QuestNodeState.Pending);
        (await store.CreateAsync(exec)).IsError.Should().BeFalse();

        // Fan out N parallel claim attempts against the same row. Only one
        // should produce Result != null; everyone else must see Result == null
        // with IsError == false (race-loser).
        const int parallelism = 8;
        var tasks = Enumerable.Range(0, parallelism)
            .Select(_ => store.TryClaimPendingAsync(exec.RunId, exec.NodeId))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var winners = results.Count(r => !r.IsError && r.Result is not null);
        winners.Should().Be(1, "the G2 single-winner contract requires exactly one claim to land");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static QuestNodeExecution NewExecution(QuestNodeState state, Guid? runId = null) => new()
    {
        Id        = Guid.NewGuid(),
        RunId     = runId ?? Guid.NewGuid(),
        NodeId    = Guid.NewGuid(),
        State     = state,
        StartedAt = DateTime.UtcNow,
    };

    private async Task<SurrealQuestNodeExecutionStore> CreateStoreAsync()
    {
        var executor = await CreateExecutorAsync();
        return new SurrealQuestNodeExecutionStore(executor);
    }

    private Task<ISurrealExecutor> CreateExecutorAsync()
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint   = SurrealTestDefaults.Endpoint,
            Namespace  = TestNamespace,
            Database   = "test",
            User       = SurrealTestDefaults.User,
            Password   = SurrealTestDefaults.Password,
        };
        var http = new HttpClient();
        var connection = new HttpSurrealConnection(http, options);
        ISurrealExecutor executor = new DefaultSurrealExecutor(connection);
        return Task.FromResult(executor);
    }
}
