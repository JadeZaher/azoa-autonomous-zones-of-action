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
/// final-hardening F4: action-dependency ENFORCEMENT at run start. The manual
/// <c>CheckDependenciesAsync</c> endpoint already existed; these tests pin the new
/// behaviour that an unsatisfied dependency now REJECTS a run at start (fail-closed),
/// on BOTH run-start paths — the legacy <c>ExecuteAsync</c> and the durable
/// <c>StartWorkflowRunAsync</c> — and that no orphaned run is created on rejection.
/// </summary>
public class ActionDependencyEnforcementTests
{
    private static (ActionManager manager,
                    InMemoryActionStore actionStore,
                    InMemoryActionRunStore runStore,
                    ActionEntity action)
        BuildActiveLinearAction(int nodeCount = 2)
    {
        var actionStore = new InMemoryActionStore();
        var runStore = new InMemoryActionRunStore();
        var execStore = new InMemoryActionNodeExecutionStore();

        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "DepGated",
            UserId = Guid.NewGuid(),
            Status = ActionStatus.Active, // must be Active to reach the run-start paths
            Nodes = Enumerable.Range(0, nodeCount).Select(i => new ActionNode
            {
                Id = Guid.NewGuid(),
                Name = $"N{i}",
                IsEntry = i == 0,
                IsTerminal = i == nodeCount - 1,
                NodeType = ActionNodeType.Condition,
                Config = "{}"
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

        actionStore.UpsertActionAsync(action).GetAwaiter().GetResult();

        var manager = new ActionManager(
            actionStore,
            runStore,
            execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        return (manager, actionStore, runStore, action);
    }

    // ─── ExecuteAsync (legacy path) ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UnsatisfiedDependency_RejectsAtStart_NoRunCreated()
    {
        var (manager, _, runStore, action) = BuildActiveLinearAction();

        // A cross-action dependency with NO Succeeded run of the depended-on action.
        var dependedOnActionId = Guid.NewGuid();
        await manager.AddDependencyAsync(action.Id, new ActionDependencyCreateModel
        {
            DependsOnActionId = dependedOnActionId
        }, action.UserId);

        var result = await manager.ExecuteAsync(action.Id, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("cannot start");

        // Fail-closed BEFORE any run row is written — no orphaned run for this action.
        var runs = await runStore.GetByActionIdAsync(action.Id);
        (runs.Result ?? Enumerable.Empty<ActionRun>()).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SatisfiedDependency_PassesTheDependencyGate()
    {
        var (manager, _, runStore, action) = BuildActiveLinearAction();

        var dependedOnActionId = Guid.NewGuid();
        await manager.AddDependencyAsync(action.Id, new ActionDependencyCreateModel
        {
            DependsOnActionId = dependedOnActionId
        }, action.UserId);

        // Satisfy it: a Succeeded run of the depended-on action.
        await runStore.CreateAsync(new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = dependedOnActionId,
            UserId = action.UserId,
            Status = ActionRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        });

        var result = await manager.ExecuteAsync(action.Id, action.UserId);

        // The run is NOT rejected for the dependency reason — the gate let it through
        // (a satisfied dependency never surfaces the "cannot start" rejection).
        if (result.IsError)
            result.Message.Should().NotContain("cannot start");
    }

    // ─── StartWorkflowRunAsync (durable path) ────────────────────────────────

    [Fact]
    public async Task StartWorkflowRunAsync_UnsatisfiedDependency_RejectsAtStart_NoRunCreated()
    {
        var (manager, _, runStore, action) = BuildActiveLinearAction();

        var dependedOnActionId = Guid.NewGuid();
        await manager.AddDependencyAsync(action.Id, new ActionDependencyCreateModel
        {
            DependsOnActionId = dependedOnActionId
        }, action.UserId);

        var result = await manager.StartWorkflowRunAsync(action.Id, action.UserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("cannot start");

        var runs = await runStore.GetByActionIdAsync(action.Id);
        (runs.Result ?? Enumerable.Empty<ActionRun>()).Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflowRunAsync_NoDependencies_IsNotRejectedForDependencies()
    {
        var (manager, _, _, action) = BuildActiveLinearAction();

        // No dependencies at all — the gate is a no-op and must not reject the run.
        var result = await manager.StartWorkflowRunAsync(action.Id, action.UserId);

        if (result.IsError)
            result.Message.Should().NotContain("cannot start");
    }
}
