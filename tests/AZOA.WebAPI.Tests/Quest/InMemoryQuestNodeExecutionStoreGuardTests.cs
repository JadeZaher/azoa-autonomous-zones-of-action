using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Providers.Stores;
using Xunit;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// HIGH#7 regression tests for <see cref="InMemoryQuestNodeExecutionStore"/>:
///   * <see cref="InMemoryQuestNodeExecutionStore.UpdateAsync"/> honours the
///     optional <c>expectedState</c> guard — drift between expected and
///     actual yields an error result instead of an unconditional overwrite.
///   * Read paths return defensive clones — callers cannot mutate the
///     store's internal state through a returned reference.
/// </summary>
public class InMemoryQuestNodeExecutionStoreGuardTests
{
    private static QuestNodeExecution Seed(InMemoryQuestNodeExecutionStore store,
        Guid? runId = null, Guid? nodeId = null,
        QuestNodeState state = QuestNodeState.Pending)
    {
        var exec = new QuestNodeExecution
        {
            Id        = Guid.NewGuid(),
            RunId     = runId ?? Guid.NewGuid(),
            NodeId    = nodeId ?? Guid.NewGuid(),
            State     = state,
            StartedAt = DateTime.UtcNow,
        };
        var create = store.CreateAsync(exec).GetAwaiter().GetResult();
        create.IsError.Should().BeFalse();
        return create.Result!;
    }

    // ─── expectedState guard ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_with_expectedState_rejects_drift()
    {
        var store = new InMemoryQuestNodeExecutionStore();
        var exec  = Seed(store); // Pending

        // Caller attempts to assert "I expect this row to be Running" — but
        // the stored row is still Pending. The guard must reject.
        exec.State = QuestNodeState.Succeeded;
        var update = await store.UpdateAsync(exec, expectedState: QuestNodeState.Running);

        update.IsError.Should().BeTrue();
        update.Message.Should().Contain("state-machine guard")
                                .And.Contain("expected=Running")
                                .And.Contain("actual=Pending");

        // The stored row must NOT have moved.
        var roundTrip = await store.GetByIdAsync(exec.Id);
        roundTrip.Result!.State.Should().Be(QuestNodeState.Pending,
            "guard-rejected updates must not partially apply");
    }

    [Fact]
    public async Task UpdateAsync_with_expectedState_accepts_match()
    {
        var store = new InMemoryQuestNodeExecutionStore();
        var exec  = Seed(store, state: QuestNodeState.Running);

        exec.State   = QuestNodeState.Succeeded;
        exec.EndedAt = DateTime.UtcNow;
        var update = await store.UpdateAsync(exec, expectedState: QuestNodeState.Running);

        update.IsError.Should().BeFalse();
        update.Result!.State.Should().Be(QuestNodeState.Succeeded);
    }

    [Fact]
    public async Task UpdateAsync_without_expectedState_is_unconditional()
    {
        var store = new InMemoryQuestNodeExecutionStore();
        var exec  = Seed(store, state: QuestNodeState.Pending);

        exec.State = QuestNodeState.Skipped;
        var update = await store.UpdateAsync(exec); // no guard

        update.IsError.Should().BeFalse();
        update.Result!.State.Should().Be(QuestNodeState.Skipped,
            "null expectedState preserves the historic unconditional-overwrite behaviour");
    }

    // ─── Defensive copies ───────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_returns_defensive_copy()
    {
        var store = new InMemoryQuestNodeExecutionStore();
        var seeded = Seed(store, state: QuestNodeState.Pending);

        var first = (await store.GetByIdAsync(seeded.Id)).Result!;
        first.State = QuestNodeState.Cancelled; // mutate the returned reference

        // Second read must see the original state — the first read's
        // mutation cannot have leaked into the store.
        var second = (await store.GetByIdAsync(seeded.Id)).Result!;
        second.State.Should().Be(QuestNodeState.Pending,
            "GetByIdAsync must return a clone, not the store's live reference");
    }

    [Fact]
    public async Task GetByRunAndNodeAsync_returns_defensive_copy()
    {
        var store = new InMemoryQuestNodeExecutionStore();
        var seeded = Seed(store, state: QuestNodeState.Pending);

        var first = (await store.GetByRunAndNodeAsync(seeded.RunId, seeded.NodeId)).Result!;
        first.State  = QuestNodeState.Failed;
        first.Output = "side-effect via returned reference";

        var second = (await store.GetByRunAndNodeAsync(seeded.RunId, seeded.NodeId)).Result!;
        second.State.Should().Be(QuestNodeState.Pending);
        second.Output.Should().BeNull();
    }

    [Fact]
    public async Task GetByRunIdAsync_returns_defensive_copies_per_row()
    {
        var store = new InMemoryQuestNodeExecutionStore();
        var runId = Guid.NewGuid();
        var a = Seed(store, runId: runId, state: QuestNodeState.Pending);
        var b = Seed(store, runId: runId, state: QuestNodeState.Pending);

        var firstList = (await store.GetByRunIdAsync(runId)).Result!.ToList();
        firstList.Should().HaveCount(2);
        foreach (var row in firstList) row.State = QuestNodeState.Cancelled;

        // Read again — both rows must still be Pending.
        var secondList = (await store.GetByRunIdAsync(runId)).Result!.ToList();
        secondList.All(r => r.State == QuestNodeState.Pending).Should().BeTrue(
            "GetByRunIdAsync must clone each row before handing it back");
    }

    // ─── Race scenario: in-flight succeed concurrent with ForkAsync cancel ─

    [Fact]
    public async Task ForkAsync_race_with_in_flight_node_succeed_does_not_orphan()
    {
        // The unacceptable outcome the spec calls out: "succeeded execution
        // silently turned into a skipped cancel and the manager believes it
        // was handled." With the HIGH#7 guard in place, either:
        //   (a) the succeed-write lands first and the cancel-write rejects
        //       (manager keeps Succeeded), OR
        //   (b) the cancel-write lands first and the succeed-write rejects
        //       (manager observes the rejection and re-reads — accepting
        //       Cancelled). Both outcomes are acceptable; the rejection
        //       observability is what matters.
        var store = new InMemoryQuestNodeExecutionStore();
        var exec = Seed(store, state: QuestNodeState.Running);

        // Caller A: simulates the per-node execute path completing Running → Succeeded.
        var succeeded = new QuestNodeExecution
        {
            Id        = exec.Id,
            RunId     = exec.RunId,
            NodeId    = exec.NodeId,
            State     = QuestNodeState.Succeeded,
            Output    = "result",
            StartedAt = exec.StartedAt,
            EndedAt   = DateTime.UtcNow,
        };

        // Caller B: simulates ForkAsync seeing Running → Cancelled.
        var cancelled = new QuestNodeExecution
        {
            Id        = exec.Id,
            RunId     = exec.RunId,
            NodeId    = exec.NodeId,
            State     = QuestNodeState.Cancelled,
            StartedAt = exec.StartedAt,
            EndedAt   = DateTime.UtcNow,
        };

        var first  = await store.UpdateAsync(succeeded, expectedState: QuestNodeState.Running);
        var second = await store.UpdateAsync(cancelled, expectedState: QuestNodeState.Running);

        // Exactly one writer must have observed a guard rejection.
        (first.IsError || second.IsError).Should().BeTrue(
            "at least one of the two state-machine guards must reject when both " +
            "claim Running as the pre-state");
        (first.IsError && second.IsError).Should().BeFalse(
            "both updates rejecting would be a bug — the first write should have succeeded");

        var stored = (await store.GetByIdAsync(exec.Id)).Result!;
        // Whichever landed first wins; the other was rejected with an error
        // result that the manager can observe and react to.
        stored.State.Should().BeOneOf(QuestNodeState.Succeeded, QuestNodeState.Cancelled);
    }
}
