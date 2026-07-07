using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// final-hardening F4: quest-dependency ENFORCEMENT at run start. The manual
/// <c>CheckDependenciesAsync</c> endpoint already existed; these tests pin the new
/// behaviour that an unsatisfied dependency now REJECTS a run at start (fail-closed),
/// on BOTH run-start paths — the legacy <c>ExecuteAsync</c> and the durable
/// <c>StartWorkflowRunAsync</c> — and that no orphaned run is created on rejection.
/// </summary>
public class QuestDependencyEnforcementTests
{
    private static (QuestManager manager,
                    InMemoryQuestStore questStore,
                    InMemoryQuestRunStore runStore,
                    QuestEntity quest)
        BuildActiveLinearQuest(int nodeCount = 2)
    {
        var questStore = new InMemoryQuestStore();
        var runStore = new InMemoryQuestRunStore();
        var execStore = new InMemoryQuestNodeExecutionStore();

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "DepGated",
            AvatarId = Guid.NewGuid(),
            Status = QuestStatus.Active, // must be Active to reach the run-start paths
            Nodes = Enumerable.Range(0, nodeCount).Select(i => new QuestNode
            {
                Id = Guid.NewGuid(),
                Name = $"N{i}",
                IsEntry = i == 0,
                IsTerminal = i == nodeCount - 1,
                NodeType = QuestNodeType.Condition,
                Config = "{}"
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

        questStore.UpsertQuestAsync(quest).GetAwaiter().GetResult();

        var manager = new QuestManager(
            questStore,
            runStore,
            execStore,
            new QuestDagValidator(), new QuestDagExecutabilityValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        return (manager, questStore, runStore, quest);
    }

    // ─── ExecuteAsync (legacy path) ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UnsatisfiedDependency_RejectsAtStart_NoRunCreated()
    {
        var (manager, _, runStore, quest) = BuildActiveLinearQuest();

        // A cross-quest dependency with NO Succeeded run of the depended-on quest.
        var dependedOnQuestId = Guid.NewGuid();
        await manager.AddDependencyAsync(quest.Id, new QuestDependencyCreateModel
        {
            DependsOnQuestId = dependedOnQuestId
        }, quest.AvatarId);

        var result = await manager.ExecuteAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("cannot start");

        // Fail-closed BEFORE any run row is written — no orphaned run for this quest.
        var runs = await runStore.GetByQuestIdAsync(quest.Id);
        (runs.Result ?? Enumerable.Empty<QuestRun>()).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SatisfiedDependency_PassesTheDependencyGate()
    {
        var (manager, _, runStore, quest) = BuildActiveLinearQuest();

        var dependedOnQuestId = Guid.NewGuid();
        await manager.AddDependencyAsync(quest.Id, new QuestDependencyCreateModel
        {
            DependsOnQuestId = dependedOnQuestId
        }, quest.AvatarId);

        // Satisfy it: a Succeeded run of the depended-on quest.
        await runStore.CreateAsync(new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = dependedOnQuestId,
            AvatarId = quest.AvatarId,
            Status = QuestRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow
        });

        var result = await manager.ExecuteAsync(quest.Id, quest.AvatarId);

        // The run is NOT rejected for the dependency reason — the gate let it through
        // (a satisfied dependency never surfaces the "cannot start" rejection).
        if (result.IsError)
            result.Message.Should().NotContain("cannot start");
    }

    // ─── StartWorkflowRunAsync (durable path) ────────────────────────────────

    [Fact]
    public async Task StartWorkflowRunAsync_UnsatisfiedDependency_RejectsAtStart_NoRunCreated()
    {
        var (manager, _, runStore, quest) = BuildActiveLinearQuest();

        var dependedOnQuestId = Guid.NewGuid();
        await manager.AddDependencyAsync(quest.Id, new QuestDependencyCreateModel
        {
            DependsOnQuestId = dependedOnQuestId
        }, quest.AvatarId);

        var result = await manager.StartWorkflowRunAsync(quest.Id, quest.AvatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("cannot start");

        var runs = await runStore.GetByQuestIdAsync(quest.Id);
        (runs.Result ?? Enumerable.Empty<QuestRun>()).Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflowRunAsync_NoDependencies_IsNotRejectedForDependencies()
    {
        var (manager, _, _, quest) = BuildActiveLinearQuest();

        // No dependencies at all — the gate is a no-op and must not reject the run.
        var result = await manager.StartWorkflowRunAsync(quest.Id, quest.AvatarId);

        if (result.IsError)
            result.Message.Should().NotContain("cannot start");
    }
}
