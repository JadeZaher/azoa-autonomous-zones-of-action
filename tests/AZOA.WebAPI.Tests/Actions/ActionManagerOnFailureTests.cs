using FluentAssertions;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Tests.TestSupport;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Legacy-engine OnFailure semantics: V2 skip rule + V3 handled-failure run-status.
/// AC-2a: exactly-one-arm property (both directions).
/// AC-2c: handled failure does not mark the run Failed.
/// </summary>
public class ActionManagerOnFailureTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // ── Harness ──────────────────────────────────────────────────────────────

    private static (DurableActionExecutionHarness Manager, InMemoryActionNodeExecutionStore ExecStore)
        MakeHarness(InMemoryActionStore actionStore, params IActionNodeHandler[] handlers)
    {
        var execStore = new InMemoryActionNodeExecutionStore();
        var manager = new DurableActionExecutionHarness(
            actionStore,
            new InMemoryActionRunStore(),
            execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(handlers),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());
        return (manager, execStore);
    }

    // ── Action factory: Entry →(Control) success | Entry →(OnFailure) failArm ─

    private static (InMemoryActionStore Store, ActionEntity ActionDefinition,
                    Guid EntryId, Guid SuccessId, Guid FailArmId)
        BuildActionWithOnFailure(ActionNodeType entryType)
    {
        var actionId   = Guid.NewGuid();
        var entryId   = Guid.NewGuid();
        var successId = Guid.NewGuid();
        var failArmId = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id = actionId, UserId = UserId,
            Name = "OnFailureTest", Status = ActionStatus.Active,
            Nodes =
            [
                new ActionNode
                {
                    Id = entryId, ActionId = actionId, Name = "entry",
                    NodeType = entryType,
                    IsEntry = true, IsTerminal = false, ExecutionOrder = 0
                },
                new ActionNode
                {
                    Id = successId, ActionId = actionId, Name = "success-arm",
                    NodeType = ActionNodeType.Transfer,
                    IsEntry = false, IsTerminal = true, ExecutionOrder = 1
                },
                new ActionNode
                {
                    Id = failArmId, ActionId = actionId, Name = "failure-arm",
                    NodeType = ActionNodeType.Transfer,
                    IsEntry = false, IsTerminal = true, ExecutionOrder = 2
                },
            ],
            Edges =
            [
                new ActionEdge
                {
                    Id = Guid.NewGuid(), ActionId = actionId,
                    SourceNodeId = entryId, TargetNodeId = successId,
                    EdgeType = ActionEdgeType.Control
                },
                new ActionEdge
                {
                    Id = Guid.NewGuid(), ActionId = actionId,
                    SourceNodeId = entryId, TargetNodeId = failArmId,
                    EdgeType = ActionEdgeType.OnFailure
                },
            ]
        };

        var store = new InMemoryActionStore();
        store.UpsertActionAsync(action).GetAwaiter().GetResult();
        return (store, action, entryId, successId, failArmId);
    }

    // ── AC-2a / AC-2c: source Fails → failure arm runs, success arm skipped,
    //                   run Succeeded (handled failure, V3) ────────────────────

    [Fact]
    public async Task OnFailure_SourceFails_FailArmSucceeds_SuccessArmSkipped_RunSucceeded()
    {
        var (store, action, entryId, successId, failArmId) =
            BuildActionWithOnFailure(ActionNodeType.GateCheck);

        var (manager, execStore) = MakeHarness(store,
            new AlwaysFailHandler(ActionNodeType.GateCheck),
            new AlwaysSucceedHandler(ActionNodeType.Transfer));

        var result = await manager.ExecuteAsync(action.Id, UserId);

        result.IsError.Should().BeFalse(because: result.Message ?? "no error");
        result.Result!.Status.Should().Be(ActionRunStatus.Succeeded,
            because: "Failed node has an OnFailure edge → handled (V3) → run Succeeded");

        var execs = (await execStore.GetByRunIdAsync(result.Result!.Id)).Result!.ToList();
        execs.Should().HaveCount(3);

        Exec(execs, entryId).State.Should().Be(ActionNodeState.Failed);
        Exec(execs, failArmId).State.Should().Be(ActionNodeState.Succeeded,
            because: "failure arm activated when source Failed");
        Exec(execs, successId).State.Should().Be(ActionNodeState.Skipped,
            because: "success arm skipped when source Failed");
    }

    // ── AC-2a: source Succeeds → success arm runs, failure arm skipped ────────

    [Fact]
    public async Task OnFailure_SourceSucceeds_SuccessArmRuns_FailArmSkipped()
    {
        var (store, action, entryId, successId, failArmId) =
            BuildActionWithOnFailure(ActionNodeType.Transfer);

        var (manager, execStore) = MakeHarness(store,
            new AlwaysSucceedHandler(ActionNodeType.Transfer));

        var result = await manager.ExecuteAsync(action.Id, UserId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionRunStatus.Succeeded);

        var execs = (await execStore.GetByRunIdAsync(result.Result!.Id)).Result!.ToList();
        Exec(execs, entryId).State.Should().Be(ActionNodeState.Succeeded);
        Exec(execs, successId).State.Should().Be(ActionNodeState.Succeeded,
            because: "success arm runs when source Succeeded");
        Exec(execs, failArmId).State.Should().Be(ActionNodeState.Skipped,
            because: "failure arm skipped when source Succeeded (inverse activation)");
    }

    // ── AC-2c: unhandled failure (no OnFailure edge) marks run Failed ─────────

    [Fact]
    public async Task NoOnFailureEdge_SourceFails_WaitsForSagaCompensation()
    {
        var actionId = Guid.NewGuid();
        var nodeId  = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id = actionId, UserId = UserId,
            Name = "UnhandledFail", Status = ActionStatus.Active,
            Nodes =
            [
                new ActionNode
                {
                    Id = nodeId, ActionId = actionId, Name = "broken",
                    NodeType = ActionNodeType.Transfer,
                    IsEntry = true, IsTerminal = true, ExecutionOrder = 0
                },
            ],
            Edges = []
        };

        var store = new InMemoryActionStore();
        await store.UpsertActionAsync(action);

        var (manager, _) = MakeHarness(store,
            new AlwaysFailHandler(ActionNodeType.Transfer));

        var result = await manager.ExecuteAsync(action.Id, UserId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionRunStatus.AwaitingReconciliation,
            because: "an unhandled durable failure must pass through saga compensation before terminal failure");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActionNodeExecution Exec(List<ActionNodeExecution> execs, Guid nodeId) =>
        execs.Single(e => e.NodeId == nodeId);

    private sealed class AlwaysSucceedHandler(ActionNodeType type) : IActionNodeHandler
    {
        public ActionNodeType NodeType => type;
        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(ActionNodeHandlerResult.Ok(output: null));
    }

    private sealed class AlwaysFailHandler(ActionNodeType type) : IActionNodeHandler
    {
        public ActionNodeType NodeType => type;
        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(ActionNodeHandlerResult.Fail("forced failure"));
    }
}
