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
/// Pins the cascade-skip semantics introduced by action-dag-semantic-hardening
/// (FR-1 / AC-1a–1d). See Managers/AGENTS.md §skip-semantics.
/// </summary>
public class ActionManagerSkipPropagationTests
{
    // ─── Scaffolding ───────────────────────────────────────────────────

    /// <summary>
    /// Builds an action with a GateCheck →(Control) Transfer1 →(Control) Transfer2
    /// chain. GateCheck is wired to a failing handler so it produces Failed.
    /// Returns the manager + run after execution so callers can inspect node states.
    /// </summary>
    private static async Task<(DurableActionExecutionHarness manager,
                                InMemoryActionNodeExecutionStore execStore,
                                ActionEntity action,
                                ActionRun run)>
        BuildAndExecuteGateChainAsync(bool gateCheckPasses)
    {
        var actionStore = new InMemoryActionStore();
        var runStore   = new InMemoryActionRunStore();
        var execStore  = new InMemoryActionNodeExecutionStore();

        var gateId  = Guid.NewGuid();
        var t1Id    = Guid.NewGuid();
        var t2Id    = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Gate chain",
            UserId = userId,
            Status   = ActionStatus.Active,
            Nodes = new List<ActionNode>
            {
                new() { Id = gateId, Name = "Gate", NodeType = ActionNodeType.GateCheck,
                        Config = "{\"predicate\":\"true\",\"reads\":{},\"holons\":[]}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = t1Id, Name = "Transfer1", NodeType = ActionNodeType.Transfer,
                        Config = "{\"nftId\":\"00000000000000000000000000000001\",\"request\":{}}",
                        IsEntry = false, IsTerminal = false },
                new() { Id = t2Id, Name = "Transfer2", NodeType = ActionNodeType.Transfer,
                        Config = "{\"nftId\":\"00000000000000000000000000000002\",\"request\":{}}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = Guid.Empty, SourceNodeId = gateId,  TargetNodeId = t1Id, EdgeType = ActionEdgeType.Control },
                new() { Id = Guid.NewGuid(), ActionId = Guid.Empty, SourceNodeId = t1Id,   TargetNodeId = t2Id, EdgeType = ActionEdgeType.Control },
            }
        };

        // Assign topological order manually (validator not run here).
        action.Nodes[0].ExecutionOrder = 0;
        action.Nodes[1].ExecutionOrder = 1;
        action.Nodes[2].ExecutionOrder = 2;

        await actionStore.UpsertActionAsync(action);

        // A handler that honours gateCheckPasses for GateCheck; Transfer handlers
        // are irrelevant when skipped but must be present so the registry doesn't
        // short-circuit.
        var handlers = new IActionNodeHandler[]
        {
            new ConfigurableGateCheckHandler(passes: gateCheckPasses),
            new AlwaysSucceedHandler(ActionNodeType.Transfer),
        };

        var manager = new DurableActionExecutionHarness(
            actionStore,
            runStore,
            execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(handlers),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(action.Id, userId);
        runResult.IsError.Should().BeFalse("execution should succeed even when gate fails");

        return (manager, execStore, action, runResult.Result!);
    }

    // ─── AC-1a: GateCheck →(Control) Transfer1 →(Control) Transfer2 ───────────
    // Gate fails ⇒ BOTH Transfer1 AND Transfer2 must be Skipped.

    [Fact]
    public async Task Control_Chain_GateFails_BothSuccessorsSkipped()
    {
        var (_, execStore, action, run) = await BuildAndExecuteGateChainAsync(gateCheckPasses: false);

        var executions = (await execStore.GetByRunIdAsync(run.Id)).Result!.ToList();

        var gateExec  = executions.First(e => e.NodeId == action.Nodes[0].Id);
        var t1Exec    = executions.First(e => e.NodeId == action.Nodes[1].Id);
        var t2Exec    = executions.First(e => e.NodeId == action.Nodes[2].Id);

        gateExec.State.Should().Be(ActionNodeState.Failed,    "gate predicate failed");
        t1Exec.State.Should().Be(ActionNodeState.Skipped,     "Transfer1 behind failed gate");
        t2Exec.State.Should().Be(ActionNodeState.Skipped,     "Transfer2 behind skipped Transfer1 (cascade)");
    }

    [Fact]
    public async Task Control_Chain_GatePasses_BothSuccessorsSucceeded()
    {
        var (_, execStore, action, run) = await BuildAndExecuteGateChainAsync(gateCheckPasses: true);

        var executions = (await execStore.GetByRunIdAsync(run.Id)).Result!.ToList();
        var t2Exec = executions.First(e => e.NodeId == action.Nodes[2].Id);

        // Happy path: none skipped.
        t2Exec.State.Should().Be(ActionNodeState.Succeeded, "all nodes run when gate passes");
    }

    // ─── AC-1b: Conditional edge with EMPTY Condition skips on Failed/Skipped ──

    [Fact]
    public async Task Conditional_EmptyCondition_SourceFailed_TargetSkipped()
    {
        var actionStore = new InMemoryActionStore();
        var runStore   = new InMemoryActionRunStore();
        var execStore  = new InMemoryActionNodeExecutionStore();

        var srcId  = Guid.NewGuid();
        var tgtId  = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Conditional empty",
            UserId = userId,
            Status   = ActionStatus.Active,
            Nodes = new List<ActionNode>
            {
                new() { Id = srcId, Name = "Src", NodeType = ActionNodeType.GateCheck,
                        Config = "{\"predicate\":\"true\",\"reads\":{},\"holons\":[]}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = tgtId, Name = "Tgt", NodeType = ActionNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = Guid.Empty,
                        SourceNodeId = srcId, TargetNodeId = tgtId,
                        EdgeType = ActionEdgeType.Conditional,
                        Condition = "" },   // intentionally empty
            }
        };
        action.Nodes[0].ExecutionOrder = 0;
        action.Nodes[1].ExecutionOrder = 1;
        await actionStore.UpsertActionAsync(action);

        var manager = new DurableActionExecutionHarness(
            actionStore, runStore, execStore, new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[]
            {
                new ConfigurableGateCheckHandler(passes: false),
                new AlwaysSucceedHandler(ActionNodeType.HolonGet),
            }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(action.Id, userId);
        runResult.IsError.Should().BeFalse();

        var executions = (await execStore.GetByRunIdAsync(runResult.Result!.Id)).Result!.ToList();
        executions.First(e => e.NodeId == tgtId).State.Should().Be(ActionNodeState.Skipped,
            "empty-condition Conditional edge propagates skip on source Failed");
    }

    [Fact]
    public async Task Conditional_EmptyCondition_SourceSkipped_TargetSkipped()
    {
        // Three-node chain: A(fails) →(Control) B →(Conditional, empty) C
        // B is Skipped (Control from Failed A); C must also be Skipped.
        var actionStore = new InMemoryActionStore();
        var runStore   = new InMemoryActionRunStore();
        var execStore  = new InMemoryActionNodeExecutionStore();

        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Cascade to conditional",
            UserId = userId,
            Status   = ActionStatus.Active,
            Nodes = new List<ActionNode>
            {
                new() { Id = aId, Name = "A", NodeType = ActionNodeType.GateCheck,
                        Config = "{\"predicate\":\"true\",\"reads\":{},\"holons\":[]}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", NodeType = ActionNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = false },
                new() { Id = cId, Name = "C", NodeType = ActionNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = Guid.Empty,
                        SourceNodeId = aId, TargetNodeId = bId,
                        EdgeType = ActionEdgeType.Control },
                new() { Id = Guid.NewGuid(), ActionId = Guid.Empty,
                        SourceNodeId = bId, TargetNodeId = cId,
                        EdgeType = ActionEdgeType.Conditional, Condition = "" },
            }
        };
        action.Nodes[0].ExecutionOrder = 0;
        action.Nodes[1].ExecutionOrder = 1;
        action.Nodes[2].ExecutionOrder = 2;
        await actionStore.UpsertActionAsync(action);

        var manager = new DurableActionExecutionHarness(
            actionStore, runStore, execStore, new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[]
            {
                new ConfigurableGateCheckHandler(passes: false),
                new AlwaysSucceedHandler(ActionNodeType.HolonGet),
            }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(action.Id, userId);
        runResult.IsError.Should().BeFalse();

        var executions = (await execStore.GetByRunIdAsync(runResult.Result!.Id)).Result!.ToList();
        executions.First(e => e.NodeId == bId).State.Should().Be(ActionNodeState.Skipped);
        executions.First(e => e.NodeId == cId).State.Should().Be(ActionNodeState.Skipped,
            "Skipped source behind empty-condition Conditional edge cascades skip");
    }

    // ─── Happy path: all predecessors Completed ⇒ target runs ──────────────────

    [Fact]
    public async Task Control_Chain_AllSucceeded_TargetRuns()
    {
        var actionStore = new InMemoryActionStore();
        var runStore   = new InMemoryActionRunStore();
        var execStore  = new InMemoryActionNodeExecutionStore();

        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id       = Guid.NewGuid(),
            Name     = "Happy chain",
            UserId = userId,
            Status   = ActionStatus.Active,
            Nodes = new List<ActionNode>
            {
                new() { Id = aId, Name = "A", NodeType = ActionNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = true, IsTerminal = false },
                new() { Id = bId, Name = "B", NodeType = ActionNodeType.HolonGet,
                        Config = "{\"id\":\"00000000000000000000000000000001\"}",
                        IsEntry = false, IsTerminal = true },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = Guid.Empty,
                        SourceNodeId = aId, TargetNodeId = bId,
                        EdgeType = ActionEdgeType.Control },
            }
        };
        action.Nodes[0].ExecutionOrder = 0;
        action.Nodes[1].ExecutionOrder = 1;
        await actionStore.UpsertActionAsync(action);

        var manager = new DurableActionExecutionHarness(
            actionStore, runStore, execStore, new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[]
            {
                new AlwaysSucceedHandler(ActionNodeType.HolonGet),
            }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var runResult = await manager.ExecuteAsync(action.Id, userId);
        runResult.IsError.Should().BeFalse();

        var executions = (await execStore.GetByRunIdAsync(runResult.Result!.Id)).Result!.ToList();
        executions.First(e => e.NodeId == bId).State.Should().Be(ActionNodeState.Succeeded,
            "no skip when predecessor succeeded");
    }

    // ─── Inline test-double handlers ────────────────────────────────────────────

    private sealed class ConfigurableGateCheckHandler : IActionNodeHandler
    {
        private readonly bool _passes;
        public ConfigurableGateCheckHandler(bool passes) => _passes = passes;
        public ActionNodeType NodeType => ActionNodeType.GateCheck;
        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(_passes
                ? ActionNodeResults.Ok("{\"pass\":true}")
                : ActionNodeResults.Fail("gate not met"));
    }

    private sealed class AlwaysSucceedHandler : IActionNodeHandler
    {
        public AlwaysSucceedHandler(ActionNodeType nodeType) => NodeType = nodeType;
        public ActionNodeType NodeType { get; }
        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext ctx, CancellationToken ct = default)
            => Task.FromResult(ActionNodeResults.Ok("{}"));
    }
}
