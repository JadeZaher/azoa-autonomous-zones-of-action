using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using AZOA.WebAPI.Tests.TestSupport;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Action temporal/fork-model — runtime lifecycle + lineage tests.
///
/// Covers (per <c>conductor/tracks/action-temporal-fork-model/plan.md</c>):
/// <list type="bullet">
///   <item>Task 14 — lineage-tree-not-validated-for-acyclicity invariant naming test.</item>
///   <item>Task 16 — re-run produces new root run; fork happy path; fork state-machine guards; lineage query order; supervisor-fail vs internal-error distinction.</item>
/// </list>
/// </summary>
public class ActionForkLineageTests
{
    // ─── Test scaffolding (real InMemory stores + real validator + Condition-only registry) ───

    private static (DurableActionExecutionHarness manager, InMemoryActionRunStore runs, InMemoryActionNodeExecutionStore execs, ActionEntity action)
        BuildLinearAction(int nodeCount = 3)
    {
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Linear",
            UserId = Guid.NewGuid(),
            Status = ActionStatus.Active,   // B6: must be Active to execute
            Nodes = Enumerable.Range(0, nodeCount).Select(i => new ActionNode
            {
                Id = Guid.NewGuid(),
                Name = $"N{i}",
                IsEntry = i == 0,
                IsTerminal = i == nodeCount - 1,
                NodeType = ActionNodeType.Condition,
                Config = "{\"i\":" + i + "}"
            }).ToList()
        };
        for (int i = 0; i < nodeCount - 1; i++)
        {
            action.Edges.Add(new ActionEdge
            {
                Id = Guid.NewGuid(),
                ActionId = action.Id,
                SourceNodeId = action.Nodes[i].Id,
                TargetNodeId = action.Nodes[i + 1].Id,
                EdgeType = ActionEdgeType.Control
            });
        }

        var store = new Mock<IActionStore>();
        store.Setup(s => s.GetActionAsync(action.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<ActionEntity> { Result = action });
        store.Setup(s => s.UpsertActionAsync(It.IsAny<ActionEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ActionEntity q, CancellationToken _) => new AZOAResult<ActionEntity> { Result = q });
        // F6: the run-start version-confirm sees an unchanged definition here.
        store.Setup(s => s.TryConfirmActionStateAsync(It.IsAny<Guid>(), It.IsAny<ActionStatus>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1);

        var runs = new InMemoryActionRunStore();
        var execs = new InMemoryActionNodeExecutionStore();
        var registry = new ActionNodeHandlerRegistry(new IActionNodeHandler[] { new ConditionNodeHandler() });
        var manager = new DurableActionExecutionHarness(store.Object, runs, execs, new ActionDagValidator(), new ActionDagExecutabilityValidator(), registry, new InMemorySagaStore(), WalletManagerMocks.Empty(), BlockchainProviderFactoryFakes.Returning(), BindingResolverFakes.PassThrough());

        return (manager, runs, execs, action);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 14 — Lineage acyclicity invariant naming test
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ActionDagValidator is the single intra-iteration acyclicity authority.
    /// Lineage (across ActionRun rows, via ParentRunId) is structurally a
    /// tree — forks branch, never merge — and is therefore not validated for
    /// acyclicity here. This test exists to name that invariant explicitly
    /// (see ADR §2.4): if a future change introduces inter-iteration cycles,
    /// it MUST come with its own validator and update this test.
    /// </summary>
    [Fact]
    public void ActionDagValidator_LineageNotValidated()
    {
        // The validator validates the intra-iteration node DAG only. There is
        // no API on it that consumes an ActionRun lineage chain; the chain is
        // built by IActionRunStore.GetLineageAsync and never enters the
        // validator. This test pins the invariant via type-level shape.
        var validator = typeof(ActionDagValidator);

        var methods = validator.GetMethods()
            .Where(m => m.DeclaringType == validator)
            .ToList();

        methods.Should().NotBeEmpty();
        // The validator must NOT expose any method that takes an ActionRun /
        // IEnumerable<ActionRun> argument — that would imply lineage is being
        // validated here, contradicting the tree-not-DAG invariant.
        methods.SelectMany(m => m.GetParameters())
               .Select(p => p.ParameterType)
               .Should().NotContain(t =>
                    t == typeof(ActionRun)
                    || (t.IsGenericType
                        && (typeof(IEnumerable<ActionRun>).IsAssignableFrom(t)
                            || t.GenericTypeArguments.Contains(typeof(ActionRun)))));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16a — Re-running a Succeeded action creates a new ROOT run
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RerunSucceededAction_CreatesNewRootRun()
    {
        var (manager, runs, _, action) = BuildLinearAction(nodeCount: 2);

        // First execution succeeds.
        var first = await manager.ExecuteAsync(action.Id, action.UserId);
        first.IsError.Should().BeFalse();
        first.Result!.Status.Should().Be(ActionRunStatus.Succeeded);
        first.Result.ParentRunId.Should().BeNull("first run is a root");

        // Second execution of the same action produces a brand-new run,
        // also a root (ParentRunId == null) — this is the "re-run vs fork"
        // distinction from ADR §2.3.
        var second = await manager.ExecuteAsync(action.Id, action.UserId);
        second.IsError.Should().BeFalse();
        second.Result!.Status.Should().Be(ActionRunStatus.Succeeded);
        second.Result.ParentRunId.Should().BeNull("re-runs of Succeeded actions are new roots, not forks");
        second.Result.Id.Should().NotBe(first.Result.Id);

        // Both rows are independently preserved (no overwrite of the prior attempt).
        var all = (await runs.GetByActionIdAsync(action.Id)).Result!.ToList();
        all.Should().HaveCount(2);
        all.Select(r => r.ParentRunId).Should().AllSatisfy(p => p.Should().BeNull());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16b — Forking a Running action creates lineage + cancels parent in-flight
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_RunningAction_CreatesChildWithLineageAndCancelsParentInFlight()
    {
        // For a deterministic fork-while-Running scenario, we do not execute
        // the action; we manually create a parent run in Running state with
        // one Succeeded and one Pending node execution, then fork at the
        // second (pending) node.
        var (manager, runs, execs, action) = BuildLinearAction(nodeCount: 3);
        // Validate to populate ExecutionOrder.
        await manager.Manager.ValidateDAGAsync(action.Id);
        var nodes = action.Nodes.OrderBy(n => n.ExecutionOrder).ToList();

        var parent = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        ActionRunSnapshot.Capture(parent, action);
        await runs.CreateAsync(parent);

        // node[0] already Succeeded
        await execs.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = parent.Id,
            NodeId = nodes[0].Id,
            State = ActionNodeState.Succeeded,
            Output = "\"done\"",
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        });
        // node[1] in flight (Running)
        await execs.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = parent.Id,
            NodeId = nodes[1].Id,
            State = ActionNodeState.Running,
            StartedAt = DateTime.UtcNow
        });
        // node[2] Pending
        await execs.CreateAsync(new ActionNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = parent.Id,
            NodeId = nodes[2].Id,
            State = ActionNodeState.Pending,
            StartedAt = DateTime.UtcNow
        });

        // Fork at node[1] — pre-history is just node[0].
        var fork = await manager.Manager.ForkAsync(parent.Id, nodes[1].Id, "trying-alternative-strategy", action.UserId);
        fork.IsError.Should().BeFalse();
        var child = fork.Result!;

        // Lineage fields set.
        child.ParentRunId.Should().Be(parent.Id);
        child.ForkedAtNodeId.Should().Be(nodes[1].Id);
        child.ForkReason.Should().Be("trying-alternative-strategy");
        child.Status.Should().Be(ActionRunStatus.Pending);

        // Parent transitioned to Forked (terminal) with EndedAt stamped.
        var reloadedParent = (await runs.GetByIdAsync(parent.Id)).Result!;
        reloadedParent.Status.Should().Be(ActionRunStatus.Forked);
        reloadedParent.EndedAt.Should().NotBeNull();

        // Parent's in-flight nodes (Running, Pending) are now Cancelled.
        var parentExecs = (await execs.GetByRunIdAsync(parent.Id)).Result!.ToList();
        parentExecs.Single(e => e.NodeId == nodes[1].Id).State.Should().Be(ActionNodeState.Cancelled);
        parentExecs.Single(e => e.NodeId == nodes[2].Id).State.Should().Be(ActionNodeState.Cancelled);
        // The previously-Succeeded node is untouched.
        parentExecs.Single(e => e.NodeId == nodes[0].Id).State.Should().Be(ActionNodeState.Succeeded);

        // Child carries the pre-fork (ExecutionOrder < forkPoint) execution
        // for node[0] — copy-by-reference semantics (ADR §2.3).
        var childExecForNode0 = (await execs.GetByRunAndNodeAsync(child.Id, nodes[0].Id)).Result;
        childExecForNode0.Should().NotBeNull();
        childExecForNode0!.State.Should().Be(ActionNodeState.Succeeded);
        childExecForNode0.Output.Should().Be("\"done\"");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16c — State-machine guard: fork on non-Running returns error
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_NonRunningRun_ReturnsError()
    {
        var (manager, _, _, action) = BuildLinearAction(nodeCount: 1);

        // Execute to completion (Status: Succeeded).
        var run = await manager.ExecuteAsync(action.Id, action.UserId);
        run.Result!.Status.Should().Be(ActionRunStatus.Succeeded);

        // Attempt fork on a Succeeded run — guard fires (ADR §2.3: only
        // Running runs are forkable; Succeeded runs are re-runnable instead).
        var fork = await manager.Manager.ForkAsync(run.Result.Id, action.Nodes[0].Id, "trying anyway", action.UserId);
        fork.IsError.Should().BeTrue();
        fork.Message.Should().Contain("Succeeded").And.Contain("Running");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16d — Fork at a node not in the action definition returns error
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_AtNodeNotInActionDefinition_ReturnsError()
    {
        var (manager, runs, _, action) = BuildLinearAction(nodeCount: 2);

        // Set up a Running parent run by hand (don't actually execute — we
        // just need the run row in Running state).
        var parent = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        ActionRunSnapshot.Capture(parent, action);
        await runs.CreateAsync(parent);

        var stranger = Guid.NewGuid(); // not in action.Nodes
        var fork = await manager.Manager.ForkAsync(parent.Id, stranger, "wrong node", action.UserId);

        fork.IsError.Should().BeTrue();
        fork.Message.Should().Contain(stranger.ToString());
        fork.Message.Should().Contain("not present");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16e — Lineage query walks parents in order
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LineageQuery_ReturnsParentChainInOrder()
    {
        var (manager, runs, _, action) = BuildLinearAction(nodeCount: 2);
        await manager.Manager.ValidateDAGAsync(action.Id);

        // Build a chain of 4 runs: root -> r1 -> r2 -> r3 (each child forked
        // from the previous). We construct them directly to keep the test
        // deterministic — the fork semantic is exercised in the dedicated
        // fork tests above.
        var root = new ActionRun { Id = Guid.NewGuid(), ActionId = action.Id, UserId = action.UserId, Status = ActionRunStatus.Forked, StartedAt = DateTime.UtcNow };
        var r1 = new ActionRun { Id = Guid.NewGuid(), ActionId = action.Id, UserId = action.UserId, Status = ActionRunStatus.Forked, StartedAt = DateTime.UtcNow, ParentRunId = root.Id };
        var r2 = new ActionRun { Id = Guid.NewGuid(), ActionId = action.Id, UserId = action.UserId, Status = ActionRunStatus.Forked, StartedAt = DateTime.UtcNow, ParentRunId = r1.Id };
        var r3 = new ActionRun { Id = Guid.NewGuid(), ActionId = action.Id, UserId = action.UserId, Status = ActionRunStatus.Running, StartedAt = DateTime.UtcNow, ParentRunId = r2.Id };
        await runs.CreateAsync(root);
        await runs.CreateAsync(r1);
        await runs.CreateAsync(r2);
        await runs.CreateAsync(r3);

        var lineage = (await runs.GetLineageAsync(r3.Id)).Result!.ToList();

        // Child-to-root order per IActionRunStore.GetLineageAsync contract.
        lineage.Select(r => r.Id).Should().Equal(r3.Id, r2.Id, r1.Id, root.Id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16f — Supervisor-driven fail carries FailReason; internal fail leaves it null
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MarkRunFailed_FromSupervisor_DistinguishedByFailReason()
    {
        // Internal-fail path: a handler throws → manager records Failed run
        // with FailReason == null and the error stored on the ActionNodeExecution.
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Failing",
            UserId = Guid.NewGuid(),
            Status = ActionStatus.Active,   // B6: must be Active to execute
            Nodes = new List<ActionNode>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Only",
                    IsEntry = true,
                    IsTerminal = true,
                    NodeType = ActionNodeType.BlockchainExecute, // no handler registered
                    Config = "{\"id\":\"" + Guid.NewGuid() + "\"}"
                }
            }
        };
        var store = new Mock<IActionStore>();
        store.Setup(s => s.GetActionAsync(action.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<ActionEntity> { Result = action });
        store.Setup(s => s.UpsertActionAsync(It.IsAny<ActionEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ActionEntity q, CancellationToken _) => new AZOAResult<ActionEntity> { Result = q });
        // F6: the run-start version-confirm sees an unchanged definition here.
        store.Setup(s => s.TryConfirmActionStateAsync(It.IsAny<Guid>(), It.IsAny<ActionStatus>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1);

        var runsStore = new InMemoryActionRunStore();
        var execStore = new InMemoryActionNodeExecutionStore();
        var manager = new DurableActionExecutionHarness(
            store.Object,
            runsStore,
            execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        // The durable step records the failure; the terminal Failed status is the saga
        // compensation's job (covered by ActionManagerOnFailureTests), not this harness's.
        var internalFail = await manager.ExecuteAsync(action.Id, action.UserId);
        internalFail.Result!.Status.Should().NotBe(ActionRunStatus.Succeeded);
        internalFail.Result.FailReason.Should().BeNull(
            "internal-error failures leave FailReason null — the error is on the ActionNodeExecution");
        var failingExec = (await execStore.GetByRunAndNodeAsync(internalFail.Result.Id, action.Nodes[0].Id)).Result!;
        failingExec.State.Should().Be(ActionNodeState.Failed);
        failingExec.Error.Should().Contain("Unsupported node type");

        // Supervisor-driven fail path: a separate Running run is explicitly
        // marked failed with a reason; FailReason carries the audit string.
        var supRun = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = action.UserId,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runsStore.CreateAsync(supRun);

        var supResult = await manager.Manager.MarkRunFailedAsync(supRun.Id, "supervisor decided", action.UserId);
        supResult.IsError.Should().BeFalse();
        supResult.Result!.Status.Should().Be(ActionRunStatus.Failed);
        supResult.Result.FailReason.Should().Be("supervisor decided");
    }
}
