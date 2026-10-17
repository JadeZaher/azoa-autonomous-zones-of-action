using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Helpers;
using System.Text.Json;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Real persistence coverage for execution identity, frozen admission and version-fenced outcomes.</summary>
public class SurrealActionNodeExecutionStoreTests : IntegrationTestBase
{
    public SurrealActionNodeExecutionStoreTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task FrozenAdmissionUsesRecordLinksAndFencesStaleCompletion()
    {
        var store = await CreateStoreAsync();
        var execution = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, execution)).IsError.Should().BeFalse();
        var found = await store.GetByRunAndNodeAsync(execution.RunId, execution.NodeId);
        found.IsError.Should().BeFalse(found.Message);
        found.Result!.FrozenInput = Frozen(found.Result, new { immutable = true });
        found.Result.WaitUntil = DateTime.UtcNow.AddSeconds(-1);
        found.Result.ExternalOperation = new(1, "test", "request", null, "hash", Guid.NewGuid(), null);
        var admitted = await store.TryAdmitAsync(found.Result, expectedRunVersion: await RunVersionAsync(execution.RunId));
        admitted.IsError.Should().BeFalse(admitted.Message);
        admitted.Result.Should().NotBeNull();
        var stale = admitted.Result!.Clone();
        var cancelled = admitted.Result.Clone();
        cancelled.State = ActionNodeState.Cancelled;
        (await store.UpdateAsync(cancelled, ActionNodeState.Running)).IsError.Should().BeFalse();
        stale.State = ActionNodeState.Succeeded;
        (await store.UpdateAsync(stale, ActionNodeState.Running)).IsError.Should().BeTrue();
        var saved = (await store.GetByRunIdAsync(execution.RunId)).Result!.Single();
        saved.State.Should().Be(ActionNodeState.Cancelled);
        saved.FrozenInput.Should().Be(found.Result.FrozenInput);
        saved.ExternalOperation.Should().Be(found.Result.ExternalOperation);
        saved.WaitUntil.Should().BeCloseTo(found.Result.WaitUntil!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ConcurrentFrozenAdmissionsHaveOneWinnerAndRetainItsPayload()
    {
        var store = await CreateStoreAsync();
        var execution = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, execution)).IsError.Should().BeFalse();
        var candidates = Enumerable.Range(0, 8).Select(index =>
        {
            var candidate = execution.Clone();
            candidate.FrozenInput = Frozen(candidate, new { candidate = index });
            return candidate;
        }).ToArray();
        var runVersion = await RunVersionAsync(execution.RunId);
        var outcomes = await Task.WhenAll(candidates.Select(candidate => store.TryAdmitAsync(candidate, expectedRunVersion: runVersion)));
        var winner = outcomes.Where(outcome => !outcome.IsError && outcome.Result is not null).Should().ContainSingle().Subject;
        var saved = (await store.GetByRunAndNodeAsync(execution.RunId, execution.NodeId)).Result!;
        saved.FrozenInput.Should().Be(winner.Result!.FrozenInput);
        saved.State.Should().Be(ActionNodeState.Running);
        saved.Version.Should().Be(1);
    }

    // ── Basic CRUD ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Create_GetById_RoundTripsAllFields()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, exec)).IsError.Should().BeFalse();

        var fetched = await store.GetByIdAsync(exec.Id);
        fetched.IsError.Should().BeFalse(fetched.Message);
        fetched.Result.Should().NotBeNull();
        fetched.Result!.Id.Should().Be(exec.Id);
        fetched.Result.RunId.Should().Be(exec.RunId);
        fetched.Result.NodeId.Should().Be(exec.NodeId);
        fetched.Result.State.Should().Be(ActionNodeState.Pending);
        fetched.Result.StartedAt.Should().BeCloseTo(exec.StartedAt, TimeSpan.FromSeconds(2));
    }

    [SkippableFact]
    public async Task GetByRunAndNode_FindsExecution()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, exec)).IsError.Should().BeFalse();

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
        var a = NewExecution(ActionNodeState.Pending, runId: runId);
        var b = NewExecution(ActionNodeState.Pending, runId: runId);
        a.StartedAt = t0;
        b.StartedAt = t0.AddSeconds(5);

        (await SeedExecutionAsync(store, a)).IsError.Should().BeFalse();
        (await SeedExecutionAsync(store, b)).IsError.Should().BeFalse();

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

        var exec = NewExecution(ActionNodeState.Running);
        (await SeedExecutionAsync(store, exec)).IsError.Should().BeFalse();

        exec.State   = ActionNodeState.Succeeded;
        exec.Output  = "{\"holonId\":\"abc\"}";
        exec.TxHash  = "sim:action-terminal-state";
        exec.EndedAt = DateTime.UtcNow;

        var updated = await store.UpdateAsync(exec);
        updated.IsError.Should().BeFalse(updated.Message);

        var fetched = await store.GetByIdAsync(exec.Id);
        fetched.Result!.State.Should().Be(ActionNodeState.Succeeded);
        fetched.Result.Output.Should().Be("{\"holonId\":\"abc\"}");
        fetched.Result.TxHash.Should().Be("sim:action-terminal-state");
        fetched.Result.EndedAt.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Update_WithExpectedStateGuard_RejectsOnDrift()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, exec)).IsError.Should().BeFalse();

        // Caller asserts the row should still be Running, but it is Pending.
        exec.State = ActionNodeState.Succeeded;
        var guarded = await store.UpdateAsync(exec, expectedState: ActionNodeState.Running);
        guarded.IsError.Should().BeTrue("state-machine guard must reject the drift");
    }


    [SkippableFact]
    public async Task TryAdmit_FirstCaller_Wins_TransitionsToRunning()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, exec)).IsError.Should().BeFalse();

        exec.FrozenInput = Frozen(exec, new { });
        var claim = await store.TryAdmitAsync(exec, expectedRunVersion: await RunVersionAsync(exec.RunId));
        claim.IsError.Should().BeFalse(claim.Message);
        claim.Result.Should().NotBeNull("first caller must win the conditional claim");
        claim.Result!.State.Should().Be(ActionNodeState.Running);
    }

    [SkippableFact]
    public async Task TryAdmit_StaleSecondCaller_Loses_ReturnsNullWithoutError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, exec)).IsError.Should().BeFalse();

        exec.FrozenInput = Frozen(exec, new { });
        var runVersion = await RunVersionAsync(exec.RunId);
        var first = await store.TryAdmitAsync(exec.Clone(), expectedRunVersion: runVersion);
        var second = await store.TryAdmitAsync(exec.Clone(), expectedRunVersion: runVersion);

        first.Result.Should().NotBeNull("first caller wins");
        second.IsError.Should().BeFalse("losing the race is not an error");
        second.Result.Should().BeNull("loser sees no row — exactly-one-winner contract");

        var still = await store.GetByIdAsync(exec.Id);
        still.Result!.State.Should().Be(ActionNodeState.Running,
            "loser must not regress the state of the winner's row");
    }

    [SkippableFact]
    public async Task TryAdmit_NoSuchExecution_DoesNotClaimOrAdvanceRun()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var execution = NewExecution(ActionNodeState.Pending);
        await SeedRunAsync(execution.RunId);
        var version = await RunVersionAsync(execution.RunId);
        execution.FrozenInput = Frozen(execution, new { });
        var miss = await store.TryAdmitAsync(execution, expectedRunVersion: version);
        miss.Result.Should().BeNull();
        (await RunVersionAsync(execution.RunId)).Should().Be(version);
    }

    [SkippableFact]
    public async Task TryAdmit_ConcurrentRace_ExactlyOneWinner()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var exec = NewExecution(ActionNodeState.Pending);
        (await SeedExecutionAsync(store, exec)).IsError.Should().BeFalse();

        const int parallelism = 8;
        exec.FrozenInput = Frozen(exec, new { });
        var runVersion = await RunVersionAsync(exec.RunId);
        var tasks = Enumerable.Range(0, parallelism)
            .Select(_ => store.TryAdmitAsync(exec.Clone(), expectedRunVersion: runVersion))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(result => !result.IsError);
        var winners = results.Count(r => !r.IsError && r.Result is not null);
        winners.Should().Be(1, "the G2 single-winner contract requires exactly one claim to land");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActionNodeExecution NewExecution(ActionNodeState state, Guid? runId = null) => new()
    {
        Id        = Guid.NewGuid(),
        RunId     = runId ?? Guid.NewGuid(),
        NodeId    = Guid.NewGuid(),
        State     = state,
        StartedAt = DateTime.UtcNow,
    };

    private async Task<AZOA.WebAPI.Models.Responses.AZOAResult<ActionNodeExecution>> SeedExecutionAsync(
        SurrealActionNodeExecutionStore store, ActionNodeExecution execution)
    {
        await SeedRunAsync(execution.RunId);
        return await store.CreateAsync(execution);
    }

    private async Task SeedRunAsync(Guid runId)
    {
        var runs = new SurrealActionRunStore(await CreateExecutorAsync());
        if ((await runs.GetByIdAsync(runId)).Result is not null) return;
        var result = await runs.CreateAsync(new ActionRun
        {
            Id = runId, ActionId = Guid.NewGuid(), UserId = runId,
            Status = ActionRunStatus.Running, StartedAt = DateTime.UtcNow
        });
        result.IsError.Should().BeFalse(result.Message);
    }

    private async Task<long> RunVersionAsync(Guid runId)
    {
        var result = await new SurrealActionRunStore(await CreateExecutorAsync()).GetByIdAsync(runId);
        result.IsError.Should().BeFalse(result.Message);
        return result.Result!.Version;
    }

    private static string Frozen(ActionNodeExecution node, object configuration)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new ActionFrozenNodeInput(1, node.RunId, node.NodeId,
            ActionNodeType.HolonGet, node.RunId, null, JsonSerializer.SerializeToElement(configuration),
            new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>())));

    private async Task<SurrealActionNodeExecutionStore> CreateStoreAsync()
    {
        var executor = await CreateExecutorAsync();
        return new SurrealActionNodeExecutionStore(executor);
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
        var connection = new SurrealDbNetConnection(options);
        ISurrealExecutor executor = new DefaultSurrealExecutor(connection);
        return Task.FromResult(executor);
    }
}
