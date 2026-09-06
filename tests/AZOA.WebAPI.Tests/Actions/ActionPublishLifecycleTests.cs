using FluentAssertions;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Pins the publish-gate / definition-lifecycle semantics (FR-2 + FR-3 / AC-2a–2e + AC-3a/3b).
/// See Managers/AGENTS.md §publish-lifecycle.
/// </summary>
public class ActionPublishLifecycleTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // ─── helpers ───────────────────────────────────────────────────────────────

    private static (ActionManager manager, InMemoryActionStore actionStore, InMemoryActionRunStore runStore)
        BuildManager(ActionEntity? action = null)
    {
        var actionStore = new InMemoryActionStore();
        var runStore   = new InMemoryActionRunStore();
        if (action != null)
            actionStore.UpsertActionAsync(action).GetAwaiter().GetResult();

        var manager = new ActionManager(
            actionStore,
            runStore,
            new InMemoryActionNodeExecutionStore(),
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        return (manager, actionStore, runStore);
    }

    /// <summary>Minimal valid linear 2-node action in Draft state.</summary>
    private static ActionEntity ValidDraftAction(Guid? userId = null)
    {
        var entryId    = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var qid = Guid.NewGuid();
        return new ActionEntity
        {
            Id       = qid,
            Name     = "Valid draft",
            UserId = userId ?? UserId,
            Status   = ActionStatus.Draft,
            Nodes = new List<ActionNode>
            {
                new() { Id = entryId,    Name = "Entry",    NodeType = ActionNodeType.Condition,
                        IsEntry = true, IsTerminal = false, Config = "{}" },
                new() { Id = terminalId, Name = "Terminal", NodeType = ActionNodeType.Condition,
                        IsEntry = false, IsTerminal = true, Config = "{}" },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = qid,
                        SourceNodeId = entryId, TargetNodeId = terminalId,
                        EdgeType = ActionEdgeType.Control },
            }
        };
    }

    // ─── AC-2a: publish valid action → Active ──────────────────────────────────

    [Fact]
    public async Task Publish_ValidAction_FlipsToDraftToActive()
    {
        var action = ValidDraftAction();
        var (manager, _, _) = BuildManager(action);

        var result = await manager.PublishAsync(action.Id, action.UserId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionStatus.Active);
    }

    // ─── AC-2a: publishing a cyclic action fails, action stays Draft ────────────

    [Fact]
    public async Task Publish_CyclicAction_Fails_StaysDraft()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var qid = Guid.NewGuid();

        var action = new ActionEntity
        {
            Id       = qid,
            Name     = "Cyclic",
            UserId = UserId,
            Status   = ActionStatus.Draft,
            Nodes = new List<ActionNode>
            {
                new() { Id = aId, Name = "A", IsEntry = true,  IsTerminal = false, NodeType = ActionNodeType.Condition, Config = "{}" },
                new() { Id = bId, Name = "B", IsEntry = false, IsTerminal = false, NodeType = ActionNodeType.Condition, Config = "{}" },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = qid, SourceNodeId = aId, TargetNodeId = bId, EdgeType = ActionEdgeType.Control },
                new() { Id = Guid.NewGuid(), ActionId = qid, SourceNodeId = bId, TargetNodeId = aId, EdgeType = ActionEdgeType.Control },
            }
        };
        var (manager, actionStore, _) = BuildManager(action);

        var result = await manager.PublishAsync(action.Id, action.UserId);

        result.IsError.Should().BeTrue("cyclic DAG must be rejected");
        result.Message.Should().ContainEquivalentOf("Cycle");

        // ActionDefinition must remain Draft after a failed publish.
        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result;
        reloaded!.Status.Should().Be(ActionStatus.Draft);
    }

    // ─── AC-2b: ExecuteAsync on Draft action returns error naming publish ───────

    [Fact]
    public async Task ExecuteAsync_DraftAction_ReturnsPublishRequiredError()
    {
        var action = ValidDraftAction();
        var (manager, _, _) = BuildManager(action);

        var result = await manager.ExecuteAsync(action.Id, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf("publish");
    }

    [Fact]
    public async Task StartWorkflowRunAsync_DraftAction_ReturnsPublishRequiredError()
    {
        var action = ValidDraftAction();
        var (manager, _, _) = BuildManager(action);

        var result = await manager.StartWorkflowRunAsync(action.Id, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf("publish");
    }

    // ─── AC-2c: mutations on Active action rejected; succeed after unpublish ───

    [Fact]
    public async Task AddNodeAsync_ActiveAction_Rejected()
    {
        var action = ValidDraftAction();
        action.Status = ActionStatus.Active;
        var (manager, _, _) = BuildManager(action);

        var result = await manager.AddNodeAsync(action.Id,
            new ActionNodeCreateModel { Name = "X", NodeType = ActionNodeType.Condition, Config = "{}" },
            action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf("unpublish");
    }

    [Fact]
    public async Task AddNodeAsync_AfterUnpublish_Succeeds()
    {
        var action = ValidDraftAction();
        var (manager, _, _) = BuildManager(action);

        // Publish first, then unpublish.
        await manager.PublishAsync(action.Id, action.UserId);
        await manager.UnpublishAsync(action.Id, action.UserId);

        var result = await manager.AddNodeAsync(action.Id,
            new ActionNodeCreateModel { Name = "X", NodeType = ActionNodeType.Condition, Config = "{}" },
            action.UserId);

        result.IsError.Should().BeFalse("mutations succeed after unpublish");
    }

    // ─── AC-2d: unpublish refused while in-flight run exists ──────────────────

    [Fact]
    public async Task UnpublishAsync_WithInFlightRun_Refused()
    {
        var action = ValidDraftAction();
        var (manager, _, runStore) = BuildManager(action);

        // Publish the action.
        await manager.PublishAsync(action.Id, action.UserId);

        // Seed a non-terminal run directly into the store.
        await runStore.CreateAsync(new ActionRun
        {
            Id       = Guid.NewGuid(),
            ActionId  = action.Id,
            UserId = action.UserId,
            Status   = ActionRunStatus.Running,
        });

        var result = await manager.UnpublishAsync(action.Id, action.UserId);

        result.IsError.Should().BeTrue("in-flight run blocks unpublish");
        result.Message.Should().ContainEquivalentOf("in-flight");
    }

    [Fact]
    public async Task UnpublishAsync_AllRunsTerminal_Succeeds()
    {
        var action = ValidDraftAction();
        var (manager, _, runStore) = BuildManager(action);

        await manager.PublishAsync(action.Id, action.UserId);

        // Seed a terminal run.
        await runStore.CreateAsync(new ActionRun
        {
            Id       = Guid.NewGuid(),
            ActionId  = action.Id,
            UserId = action.UserId,
            Status   = ActionRunStatus.Succeeded,
        });

        var result = await manager.UnpublishAsync(action.Id, action.UserId);

        result.IsError.Should().BeFalse("all runs terminal — unpublish allowed");
        result.Result!.Status.Should().Be(ActionStatus.Draft);
    }

    // ─── AC-3a: fan-out action fails publish and StartWorkflowRunAsync ─────────

    private static ActionEntity FanOutAction()
    {
        var entryId = Guid.NewGuid();
        var b1Id    = Guid.NewGuid();
        var b2Id    = Guid.NewGuid();
        var qid     = Guid.NewGuid();
        return new ActionEntity
        {
            Id       = qid,
            Name     = "FanOut",
            UserId = UserId,
            Status   = ActionStatus.Draft,
            Nodes = new List<ActionNode>
            {
                new() { Id = entryId, Name = "Entry", IsEntry = true,  IsTerminal = false, NodeType = ActionNodeType.Condition, Config = "{}" },
                new() { Id = b1Id,    Name = "B1",    IsEntry = false, IsTerminal = true,  NodeType = ActionNodeType.Condition, Config = "{}" },
                new() { Id = b2Id,    Name = "B2",    IsEntry = false, IsTerminal = true,  NodeType = ActionNodeType.Condition, Config = "{}" },
            },
            Edges = new List<ActionEdge>
            {
                new() { Id = Guid.NewGuid(), ActionId = qid, SourceNodeId = entryId, TargetNodeId = b1Id, EdgeType = ActionEdgeType.Control },
                new() { Id = Guid.NewGuid(), ActionId = qid, SourceNodeId = entryId, TargetNodeId = b2Id, EdgeType = ActionEdgeType.Control },
            }
        };
    }

    [Fact]
    public async Task Publish_FanOutAction_Fails()
    {
        var action = FanOutAction();
        var (manager, _, _) = BuildManager(action);

        var result = await manager.PublishAsync(action.Id, action.UserId);

        result.IsError.Should().BeTrue("fan-out is rejected at publish time (AC-3a)");
        result.Message.Should().ContainEquivalentOf("fan-out");
    }

    // ─── AC-3b: same fan-out action still executes on legacy path (warning) ────

    [Fact]
    public void DagValidator_FanOut_IsWarningOnLegacyPath()
    {
        var action = FanOutAction();
        var validator = new ActionDagValidator();

        // Default call (fanOutAsError: false) → warning, not error.
        var result = validator.Validate(action, fanOutAsError: false);

        result.Warnings.Should().Contain(w => w.Contains("fan-out", StringComparison.OrdinalIgnoreCase),
            "fan-out is a warning on the legacy executor path (AC-3b)");
        result.IsValid.Should().BeTrue("fan-out warning does not fail the structural check");
    }

    [Fact]
    public void DagValidator_FanOut_IsErrorOnDurablePath()
    {
        var action = FanOutAction();
        var validator = new ActionDagValidator();

        var result = validator.Validate(action, fanOutAsError: true);

        result.IsValid.Should().BeFalse("fan-out is an error on the durable/publish path (AC-3a)");
        result.Errors.Should().Contain(e => e.Contains("fan-out", StringComparison.OrdinalIgnoreCase));
        result.Warnings.Should().BeEmpty("error path puts it in Errors, not Warnings");
    }
}
