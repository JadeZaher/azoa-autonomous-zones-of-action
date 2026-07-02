using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Quest temporal/fork-model — runtime lifecycle + lineage tests.
///
/// Covers (per <c>conductor/tracks/quest-temporal-fork-model/plan.md</c>):
/// <list type="bullet">
///   <item>Task 14 — lineage-tree-not-validated-for-acyclicity invariant naming test.</item>
///   <item>Task 16 — re-run produces new root run; fork happy path; fork state-machine guards; lineage query order; supervisor-fail vs internal-error distinction.</item>
/// </list>
/// </summary>
public class QuestForkLineageTests
{
    // ─── Test scaffolding (real InMemory stores + real validator + Condition-only registry) ───

    private static (QuestManager manager, InMemoryQuestRunStore runs, InMemoryQuestNodeExecutionStore execs, QuestEntity quest)
        BuildLinearQuest(int nodeCount = 3)
    {
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Linear",
            AvatarId = Guid.NewGuid(),
            Status = QuestStatus.Active,   // B6: must be Active to execute
            Nodes = Enumerable.Range(0, nodeCount).Select(i => new QuestNode
            {
                Id = Guid.NewGuid(),
                Name = $"N{i}",
                IsEntry = i == 0,
                IsTerminal = i == nodeCount - 1,
                NodeType = QuestNodeType.Condition,
                Config = "{\"i\":" + i + "}"
            }).ToList()
        };
        for (int i = 0; i < nodeCount - 1; i++)
        {
            quest.Edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = quest.Nodes[i].Id,
                TargetNodeId = quest.Nodes[i + 1].Id,
                EdgeType = QuestEdgeType.Control
            });
        }

        var store = new Mock<IQuestStore>();
        store.Setup(s => s.GetQuestAsync(quest.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<QuestEntity> { Result = quest });
        store.Setup(s => s.UpsertQuestAsync(It.IsAny<QuestEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((QuestEntity q, CancellationToken _) => new AZOAResult<QuestEntity> { Result = q });

        var runs = new InMemoryQuestRunStore();
        var execs = new InMemoryQuestNodeExecutionStore();
        var registry = new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { new ConditionNodeHandler() });
        var manager = new QuestManager(store.Object, runs, execs, new QuestDagValidator(), registry, new InMemorySagaStore(), WalletManagerMocks.Empty(), BlockchainProviderFactoryFakes.Returning(), BindingResolverFakes.PassThrough());

        return (manager, runs, execs, quest);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 14 — Lineage acyclicity invariant naming test
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// QuestDagValidator is the single intra-iteration acyclicity authority.
    /// Lineage (across QuestRun rows, via ParentRunId) is structurally a
    /// tree — forks branch, never merge — and is therefore not validated for
    /// acyclicity here. This test exists to name that invariant explicitly
    /// (see ADR §2.4): if a future change introduces inter-iteration cycles,
    /// it MUST come with its own validator and update this test.
    /// </summary>
    [Fact]
    public void QuestDagValidator_LineageNotValidated()
    {
        // The validator validates the intra-iteration node DAG only. There is
        // no API on it that consumes a QuestRun lineage chain; the chain is
        // built by IQuestRunStore.GetLineageAsync and never enters the
        // validator. This test pins the invariant via type-level shape.
        var validator = typeof(QuestDagValidator);

        var methods = validator.GetMethods()
            .Where(m => m.DeclaringType == validator)
            .ToList();

        methods.Should().NotBeEmpty();
        // The validator must NOT expose any method that takes a QuestRun /
        // IEnumerable<QuestRun> argument — that would imply lineage is being
        // validated here, contradicting the tree-not-DAG invariant.
        methods.SelectMany(m => m.GetParameters())
               .Select(p => p.ParameterType)
               .Should().NotContain(t =>
                    t == typeof(QuestRun)
                    || (t.IsGenericType
                        && (typeof(IEnumerable<QuestRun>).IsAssignableFrom(t)
                            || t.GenericTypeArguments.Contains(typeof(QuestRun)))));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16a — Re-running a Succeeded quest creates a new ROOT run
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RerunSucceededQuest_CreatesNewRootRun()
    {
        var (manager, runs, _, quest) = BuildLinearQuest(nodeCount: 2);

        // First execution succeeds.
        var first = await manager.ExecuteAsync(quest.Id, quest.AvatarId);
        first.IsError.Should().BeFalse();
        first.Result!.Status.Should().Be(QuestRunStatus.Succeeded);
        first.Result.ParentRunId.Should().BeNull("first run is a root");

        // Second execution of the same Quest produces a brand-new run,
        // also a root (ParentRunId == null) — this is the "re-run vs fork"
        // distinction from ADR §2.3.
        var second = await manager.ExecuteAsync(quest.Id, quest.AvatarId);
        second.IsError.Should().BeFalse();
        second.Result!.Status.Should().Be(QuestRunStatus.Succeeded);
        second.Result.ParentRunId.Should().BeNull("re-runs of Succeeded quests are new roots, not forks");
        second.Result.Id.Should().NotBe(first.Result.Id);

        // Both rows are independently preserved (no overwrite of the prior attempt).
        var all = (await runs.GetByQuestIdAsync(quest.Id)).Result!.ToList();
        all.Should().HaveCount(2);
        all.Select(r => r.ParentRunId).Should().AllSatisfy(p => p.Should().BeNull());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16b — Forking a Running quest creates lineage + cancels parent in-flight
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_RunningQuest_CreatesChildWithLineageAndCancelsParentInFlight()
    {
        // For a deterministic fork-while-Running scenario, we do not execute
        // the quest; we manually create a parent run in Running state with
        // one Succeeded and one Pending node execution, then fork at the
        // second (pending) node.
        var (manager, runs, execs, quest) = BuildLinearQuest(nodeCount: 3);
        // Validate to populate ExecutionOrder.
        await manager.ValidateDAGAsync(quest.Id);
        var nodes = quest.Nodes.OrderBy(n => n.ExecutionOrder).ToList();

        var parent = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runs.CreateAsync(parent);

        // node[0] already Succeeded
        await execs.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = parent.Id,
            NodeId = nodes[0].Id,
            State = QuestNodeState.Succeeded,
            Output = "\"done\"",
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        });
        // node[1] in flight (Running)
        await execs.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = parent.Id,
            NodeId = nodes[1].Id,
            State = QuestNodeState.Running,
            StartedAt = DateTime.UtcNow
        });
        // node[2] Pending
        await execs.CreateAsync(new QuestNodeExecution
        {
            Id = Guid.NewGuid(),
            RunId = parent.Id,
            NodeId = nodes[2].Id,
            State = QuestNodeState.Pending,
            StartedAt = DateTime.UtcNow
        });

        // Fork at node[1] — pre-history is just node[0].
        var fork = await manager.ForkAsync(parent.Id, nodes[1].Id, "trying-alternative-strategy", quest.AvatarId);
        fork.IsError.Should().BeFalse();
        var child = fork.Result!;

        // Lineage fields set.
        child.ParentRunId.Should().Be(parent.Id);
        child.ForkedAtNodeId.Should().Be(nodes[1].Id);
        child.ForkReason.Should().Be("trying-alternative-strategy");
        child.Status.Should().Be(QuestRunStatus.Pending);

        // Parent transitioned to Forked (terminal) with EndedAt stamped.
        var reloadedParent = (await runs.GetByIdAsync(parent.Id)).Result!;
        reloadedParent.Status.Should().Be(QuestRunStatus.Forked);
        reloadedParent.EndedAt.Should().NotBeNull();

        // Parent's in-flight nodes (Running, Pending) are now Cancelled.
        var parentExecs = (await execs.GetByRunIdAsync(parent.Id)).Result!.ToList();
        parentExecs.Single(e => e.NodeId == nodes[1].Id).State.Should().Be(QuestNodeState.Cancelled);
        parentExecs.Single(e => e.NodeId == nodes[2].Id).State.Should().Be(QuestNodeState.Cancelled);
        // The previously-Succeeded node is untouched.
        parentExecs.Single(e => e.NodeId == nodes[0].Id).State.Should().Be(QuestNodeState.Succeeded);

        // Child carries the pre-fork (ExecutionOrder < forkPoint) execution
        // for node[0] — copy-by-reference semantics (ADR §2.3).
        var childExecForNode0 = (await execs.GetByRunAndNodeAsync(child.Id, nodes[0].Id)).Result;
        childExecForNode0.Should().NotBeNull();
        childExecForNode0!.State.Should().Be(QuestNodeState.Succeeded);
        childExecForNode0.Output.Should().Be("\"done\"");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16c — State-machine guard: fork on non-Running returns error
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_NonRunningRun_ReturnsError()
    {
        var (manager, _, _, quest) = BuildLinearQuest(nodeCount: 1);

        // Execute to completion (Status: Succeeded).
        var run = await manager.ExecuteAsync(quest.Id, quest.AvatarId);
        run.Result!.Status.Should().Be(QuestRunStatus.Succeeded);

        // Attempt fork on a Succeeded run — guard fires (ADR §2.3: only
        // Running runs are forkable; Succeeded runs are re-runnable instead).
        var fork = await manager.ForkAsync(run.Result.Id, quest.Nodes[0].Id, "trying anyway", quest.AvatarId);
        fork.IsError.Should().BeTrue();
        fork.Message.Should().Contain("Succeeded").And.Contain("Running");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16d — Fork at a node not in the quest definition returns error
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_AtNodeNotInQuestDefinition_ReturnsError()
    {
        var (manager, runs, _, quest) = BuildLinearQuest(nodeCount: 2);

        // Set up a Running parent run by hand (don't actually execute — we
        // just need the run row in Running state).
        var parent = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runs.CreateAsync(parent);

        var stranger = Guid.NewGuid(); // not in quest.Nodes
        var fork = await manager.ForkAsync(parent.Id, stranger, "wrong node", quest.AvatarId);

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
        var (manager, runs, _, quest) = BuildLinearQuest(nodeCount: 2);
        await manager.ValidateDAGAsync(quest.Id);

        // Build a chain of 4 runs: root -> r1 -> r2 -> r3 (each child forked
        // from the previous). We construct them directly to keep the test
        // deterministic — the fork semantic is exercised in the dedicated
        // fork tests above.
        var root = new QuestRun { Id = Guid.NewGuid(), QuestId = quest.Id, AvatarId = quest.AvatarId, Status = QuestRunStatus.Forked, StartedAt = DateTime.UtcNow };
        var r1 = new QuestRun { Id = Guid.NewGuid(), QuestId = quest.Id, AvatarId = quest.AvatarId, Status = QuestRunStatus.Forked, StartedAt = DateTime.UtcNow, ParentRunId = root.Id };
        var r2 = new QuestRun { Id = Guid.NewGuid(), QuestId = quest.Id, AvatarId = quest.AvatarId, Status = QuestRunStatus.Forked, StartedAt = DateTime.UtcNow, ParentRunId = r1.Id };
        var r3 = new QuestRun { Id = Guid.NewGuid(), QuestId = quest.Id, AvatarId = quest.AvatarId, Status = QuestRunStatus.Running, StartedAt = DateTime.UtcNow, ParentRunId = r2.Id };
        await runs.CreateAsync(root);
        await runs.CreateAsync(r1);
        await runs.CreateAsync(r2);
        await runs.CreateAsync(r3);

        var lineage = (await runs.GetLineageAsync(r3.Id)).Result!.ToList();

        // Child-to-root order per IQuestRunStore.GetLineageAsync contract.
        lineage.Select(r => r.Id).Should().Equal(r3.Id, r2.Id, r1.Id, root.Id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 16f — Supervisor-driven fail carries FailReason; internal fail leaves it null
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MarkRunFailed_FromSupervisor_DistinguishedByFailReason()
    {
        // Internal-fail path: a handler throws → manager records Failed run
        // with FailReason == null and the error stored on the QuestNodeExecution.
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Failing",
            AvatarId = Guid.NewGuid(),
            Status = QuestStatus.Active,   // B6: must be Active to execute
            Nodes = new List<QuestNode>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Only",
                    IsEntry = true,
                    IsTerminal = true,
                    NodeType = QuestNodeType.BlockchainExecute, // no handler registered
                    Config = "{\"id\":\"" + Guid.NewGuid() + "\"}"
                }
            }
        };
        var store = new Mock<IQuestStore>();
        store.Setup(s => s.GetQuestAsync(quest.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<QuestEntity> { Result = quest });
        store.Setup(s => s.UpsertQuestAsync(It.IsAny<QuestEntity>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((QuestEntity q, CancellationToken _) => new AZOAResult<QuestEntity> { Result = q });

        var runsStore = new InMemoryQuestRunStore();
        var execStore = new InMemoryQuestNodeExecutionStore();
        var manager = new QuestManager(
            store.Object,
            runsStore,
            execStore,
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var internalFail = await manager.ExecuteAsync(quest.Id, quest.AvatarId);
        internalFail.Result!.Status.Should().Be(QuestRunStatus.Failed);
        internalFail.Result.FailReason.Should().BeNull(
            "internal-error failures leave FailReason null — the error is on the QuestNodeExecution");
        var failingExec = (await execStore.GetByRunAndNodeAsync(internalFail.Result.Id, quest.Nodes[0].Id)).Result!;
        failingExec.State.Should().Be(QuestNodeState.Failed);
        failingExec.Error.Should().Contain("Unsupported node type");

        // Supervisor-driven fail path: a separate Running run is explicitly
        // marked failed with a reason; FailReason carries the audit string.
        var supRun = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runsStore.CreateAsync(supRun);

        var supResult = await manager.MarkRunFailedAsync(supRun.Id, "supervisor decided", quest.AvatarId);
        supResult.IsError.Should().BeFalse();
        supResult.Result!.Status.Should().Be(QuestRunStatus.Failed);
        supResult.Result.FailReason.Should().Be("supervisor decided");
    }
}
