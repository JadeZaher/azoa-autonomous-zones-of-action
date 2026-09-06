using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealActionRunStore"/>.
/// Covers basic CRUD + fork lineage (parent_run_id scalar + forked_from
/// RELATE edge maintained on write) + GetLineageAsync scalar walk.
/// </summary>
public class SurrealActionRunStoreTests : IntegrationTestBase
{
    public SurrealActionRunStoreTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ── Basic CRUD ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Create_GetById_RoundTripsAllFields()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var run = NewRun(ActionRunStatus.Pending);
        var created = await store.CreateAsync(run);
        created.IsError.Should().BeFalse(created.Message);

        var fetched = await store.GetByIdAsync(run.Id);
        fetched.IsError.Should().BeFalse(fetched.Message);
        fetched.Result.Should().NotBeNull();
        fetched.Result!.Id.Should().Be(run.Id);
        fetched.Result.ActionId.Should().Be(run.ActionId);
        fetched.Result.UserId.Should().Be(run.UserId);
        fetched.Result.Status.Should().Be(ActionRunStatus.Pending);
        fetched.Result.ParentRunId.Should().BeNull();
        fetched.Result.StartedAt.Should().BeCloseTo(run.StartedAt, TimeSpan.FromSeconds(2));
    }

    [SkippableFact]
    public async Task GetById_NonExistent_ReturnsError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var miss = await store.GetByIdAsync(Guid.NewGuid());
        miss.IsError.Should().BeTrue();
        miss.Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task Update_Existing_PersistsTerminalTransition()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var run = NewRun(ActionRunStatus.Running);
        (await store.CreateAsync(run)).IsError.Should().BeFalse();

        run.Status = ActionRunStatus.Succeeded;
        run.EndedAt = DateTime.UtcNow;
        var updated = await store.UpdateAsync(run);
        updated.IsError.Should().BeFalse(updated.Message);

        var fetched = await store.GetByIdAsync(run.Id);
        fetched.Result!.Status.Should().Be(ActionRunStatus.Succeeded);
        fetched.Result.EndedAt.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Update_NonExistent_ReturnsError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var missing = NewRun(ActionRunStatus.Running);
        var res = await store.UpdateAsync(missing);
        res.IsError.Should().BeTrue("UpdateAsync must not silently CREATE a missing row");
    }

    // ── List queries ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByActionId_FiltersByActionDefinition()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var actionA = Guid.NewGuid();
        var actionB = Guid.NewGuid();

        await store.CreateAsync(NewRun(ActionRunStatus.Pending, actionId: actionA));
        await store.CreateAsync(NewRun(ActionRunStatus.Running, actionId: actionA));
        await store.CreateAsync(NewRun(ActionRunStatus.Pending, actionId: actionB));

        var fetched = await store.GetByActionIdAsync(actionA);
        fetched.IsError.Should().BeFalse();
        fetched.Result!.Should().HaveCount(2);
        fetched.Result!.Should().OnlyContain(r => r.ActionId == actionA);
    }

    [SkippableFact]
    public async Task GetByStatus_FiltersByStatus()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var pending = NewRun(ActionRunStatus.Pending);
        var running = NewRun(ActionRunStatus.Running);
        await store.CreateAsync(pending);
        await store.CreateAsync(running);

        var runs = await store.GetByStatusAsync(ActionRunStatus.Running);
        runs.IsError.Should().BeFalse();
        runs.Result!.Select(r => r.Id).Should().Contain(running.Id);
        runs.Result!.Select(r => r.Id).Should().NotContain(pending.Id);
    }

    // ── Fork lineage ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Create_WithParentRunId_PersistsParentPointer()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var parent = NewRun(ActionRunStatus.Forked);
        (await store.CreateAsync(parent)).IsError.Should().BeFalse();

        var child = NewRun(ActionRunStatus.Pending, actionId: parent.ActionId);
        child.ParentRunId = parent.Id;
        child.ForkedAtNodeId = Guid.NewGuid();
        child.ForkReason = "test-fork";

        var createRes = await store.CreateAsync(child);
        createRes.IsError.Should().BeFalse(createRes.Message);

        var fetched = await store.GetByIdAsync(child.Id);
        fetched.Result!.ParentRunId.Should().Be(parent.Id);
        fetched.Result.ForkedAtNodeId.Should().NotBeNull();
        fetched.Result.ForkReason.Should().Be("test-fork");
    }

    [SkippableFact]
    public async Task Create_Fork_AlsoCreatesForkedFromRelateEdge()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var executor = await CreateExecutorAsync();
        var store = new SurrealActionRunStore(executor);

        var parent = NewRun(ActionRunStatus.Forked);
        (await store.CreateAsync(parent)).IsError.Should().BeFalse();

        var child = NewRun(ActionRunStatus.Pending, actionId: parent.ActionId);
        child.ParentRunId = parent.Id;
        (await store.CreateAsync(child)).IsError.Should().BeFalse();

        // Probe the forked_from RELATE table directly — the §6.1 write
        // contract says "every fork creates the child action_run row AND
        // RELATE child -> forked_from -> parent in the same SurrealDB
        // transaction". COUNT the in == child / out == parent rows.
        var edgeQ = SurrealQuery
            .Of("SELECT count() AS c FROM forked_from WHERE in = type::record('action_run', $_cid) AND out = type::record('action_run', $_pid) GROUP ALL")
            .WithParam("_cid", child.Id.ToString("N").ToLowerInvariant())
            .WithParam("_pid", parent.Id.ToString("N").ToLowerInvariant());

        var rows = await executor.QueryAsync<EdgeCountProjection>(edgeQ, CancellationToken.None);
        rows.Should().NotBeEmpty("the forked_from edge must have been written alongside the child run row");
        rows[0].C.Should().Be(1, "exactly one edge from child to parent must exist");
    }

    [SkippableFact]
    public async Task GetLineage_WalksParentChainChildToRoot()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        // Build a 3-deep chain: root <- mid <- leaf
        var action = Guid.NewGuid();
        var root = NewRun(ActionRunStatus.Forked, actionId: action);
        var mid  = NewRun(ActionRunStatus.Forked, actionId: action);
        var leaf = NewRun(ActionRunStatus.Pending, actionId: action);
        mid.ParentRunId  = root.Id;
        leaf.ParentRunId = mid.Id;

        (await store.CreateAsync(root)).IsError.Should().BeFalse();
        (await store.CreateAsync(mid)).IsError.Should().BeFalse();
        (await store.CreateAsync(leaf)).IsError.Should().BeFalse();

        var lineage = await store.GetLineageAsync(leaf.Id);
        lineage.IsError.Should().BeFalse(lineage.Message);
        var chain = lineage.Result!.ToList();
        chain.Should().HaveCount(3);
        chain[0].Id.Should().Be(leaf.Id,  "lineage starts with the run itself");
        chain[1].Id.Should().Be(mid.Id);
        chain[2].Id.Should().Be(root.Id,  "lineage ends at the root run");
    }

    [SkippableFact]
    public async Task GetLineage_NonExistent_ReturnsError()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable.");
        var store = await CreateStoreAsync();

        var miss = await store.GetLineageAsync(Guid.NewGuid());
        miss.IsError.Should().BeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActionRun NewRun(ActionRunStatus status, Guid? actionId = null) => new()
    {
        Id        = Guid.NewGuid(),
        ActionId   = actionId ?? Guid.NewGuid(),
        UserId  = Guid.NewGuid(),
        Status    = status,
        StartedAt = DateTime.UtcNow,
    };

    private async Task<SurrealActionRunStore> CreateStoreAsync()
    {
        var executor = await CreateExecutorAsync();
        return new SurrealActionRunStore(executor);
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

    /// <summary>
    /// Projection for <c>SELECT count() AS c ... GROUP ALL</c>. SurrealDB
    /// returns a single row { "c": &lt;long&gt; } when at least one match
    /// exists; nothing when zero matches. The test uses <c>.NotBeEmpty()</c>
    /// to assert presence then reads <c>.C</c> for the exact count.
    /// </summary>
    private sealed class EdgeCountProjection : ISurrealRecord
    {
        public string SchemaName => "forked_from";

        [System.Text.Json.Serialization.JsonPropertyName("c")]
        public long C { get; set; }
    }
}
