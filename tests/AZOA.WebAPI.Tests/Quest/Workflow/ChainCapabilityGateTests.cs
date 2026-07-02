using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Services.Quest.Workflow;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Workflow;

/// <summary>
/// T9 — the pre-execution chain-capability gate (economic-primitive-nodes D1).
///
/// <para>Proves the locked rule at BOTH dispatch seams (the legacy synchronous
/// <see cref="QuestManager"/> executor and the durable
/// <see cref="QuestNodeStepHandler"/> saga step): a node whose handler declares
/// <see cref="IQuestNodeHandler.RequiresChainCapability"/> <c>== true</c> is
/// rejected BEFORE <c>HandleAsync</c> — and therefore before any broadcast — when
/// the run's actor has no wallet bound. A Tier-1 node (capability flag false) is
/// never gated, so a pure-metadata quest runs end to end with no wallet and no
/// chain call.</para>
/// </summary>
public sealed class ChainCapabilityGateTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();

    // ── Harness (mirrors DurableWorkflowEngineTests, parameterized on the gate) ──

    private sealed class Harness
    {
        public InMemorySagaStore SagaStore { get; } = new();
        public InMemoryQuestStore QuestStore { get; } = new();
        public InMemoryQuestRunStore RunStore { get; } = new();
        public InMemoryQuestNodeExecutionStore ExecutionStore { get; } = new();
        public required IQuestNodeHandler NodeHandler { get; init; }
        public IWalletManager WalletManager { get; init; } = WalletManagerMocks.Empty();

        public QuestManager NewManager() => new(
            QuestStore, RunStore, ExecutionStore,
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new[] { NodeHandler }),
            SagaStore, WalletManager,
            BlockchainProviderFactoryFakes.Returning());

        public (SagaProcessor Processor, ServiceProvider Scope) NewProcessor()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ISagaStore>(SagaStore);
            services.AddSingleton<IQuestStore>(QuestStore);
            services.AddSingleton<IQuestRunStore>(RunStore);
            services.AddSingleton<IQuestNodeExecutionStore>(ExecutionStore);
            services.AddSingleton<IQuestNodeHandlerRegistry>(
                new QuestNodeHandlerRegistry(new[] { NodeHandler }));
            services.AddSingleton<IWalletManager>(WalletManager);
            // QuestNodeStepHandler resolves a provider factory for reconcile-before-retry.
            services.AddSingleton(BlockchainProviderFactoryFakes.Returning());
            services.AddScoped<IStepHandler<QuestStepPayload>, QuestNodeStepHandler>();
            services.AddScoped<IStepHandler<QuestCompensatePayload>, QuestCompensateStepHandler>();

            var provider = services.BuildServiceProvider();
            var processor = new SagaProcessor(
                SagaStore,
                new SagaRegistry(new ISagaDefinition[] { new QuestWorkflowSagaDefinition() }),
                provider,
                provider.GetRequiredService<ILogger<SagaProcessor>>(),
                Options.Create(new SagaOptions()));
            return (processor, provider);
        }

        public async Task PumpAsync(SagaProcessor processor, int maxTicks = 20)
        {
            for (var tick = 0; tick < maxTicks; tick++)
            {
                SagaStore.PullForwardPendingRetries();
                if (await processor.ProcessDueStepsAsync(CancellationToken.None) == 0)
                    return;
            }
        }

        public QuestNodeExecution ExecutionFor(Guid runId, Guid nodeId) =>
            ExecutionStore.GetByRunAndNodeAsync(runId, nodeId).GetAwaiter().GetResult().Result!;
    }

    /// <summary>Tier-1 recording handler — capability flag false; never gated.</summary>
    private sealed class Tier1RecordingHandler : IQuestNodeHandler
    {
        private readonly List<Guid> _ran = new();
        public QuestNodeType NodeType { get; }
        public Tier1RecordingHandler(QuestNodeType nodeType) => NodeType = nodeType;
        public bool RequiresChainCapability => false;
        public int CountFor(Guid nodeId) { lock (_ran) return _ran.Count(id => id == nodeId); }

        public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
        {
            lock (_ran) _ran.Add(context.NodeId);
            return Task.FromResult(QuestNodeHandlerResult.Ok($"ran {context.NodeId}"));
        }
    }

    // ── Quest-definition builder (single linear chain of one node type) ──────────

    private static QuestEntity BuildLinearQuest(Harness h, QuestNodeType nodeType, params string[] names)
    {
        var questId = Guid.NewGuid();
        var nodes = new List<QuestNode>();
        for (var i = 0; i < names.Length; i++)
        {
            nodes.Add(new QuestNode
            {
                Id = Guid.NewGuid(),
                QuestId = questId,
                Name = names[i],
                NodeType = nodeType,
                Config = TransferConfigJson(),
                ExecutionOrder = i,
                IsEntry = i == 0,
                IsTerminal = i == names.Length - 1,
            });
        }
        var edges = new List<QuestEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = questId,
                SourceNodeId = nodes[i].Id,
                TargetNodeId = nodes[i + 1].Id,
                EdgeType = QuestEdgeType.Control,
            });
        }
        var quest = new QuestEntity
        {
            Id = questId, Name = "GateQuest", AvatarId = AvatarId,
            Status = QuestStatus.Active,   // B6: must be Active to execute
            Nodes = nodes, Edges = edges,
        };
        h.QuestStore.UpsertQuestAsync(quest).GetAwaiter().GetResult();
        return quest;
    }

    // A valid TransferNodeConfig body so the real TransferNodeHandler can
    // deserialize IF the gate ever lets it through (the reject test asserts it
    // never does). Tier-1 handlers ignore Config, so this is harmless for them.
    private static string TransferConfigJson() =>
        $"{{\"NftId\":\"{Guid.NewGuid()}\",\"Request\":{{\"TargetAvatarId\":\"{Guid.NewGuid()}\",\"WalletId\":\"{Guid.NewGuid()}\"}}}}";

    private static (TransferNodeHandler Handler, Mock<INftManager> Nft) NewTransferHandler(bool succeed = true)
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = !succeed, Result = null });
        return (new TransferNodeHandler(nft.Object), nft);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 1 (durable seam) — Tier-2 rejected pre-execution when no wallet bound
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DurableSeam_Tier2_NoWalletBound_FailsClosed_NoBroadcast()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, WalletManager = WalletManagerMocks.Empty() };
        var quest = BuildLinearQuest(h, QuestNodeType.Transfer, "Transfer");
        var transferNode = quest.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        var exec = h.ExecutionFor(runId, transferNode.Id);
        exec.State.Should().Be(QuestNodeState.Failed, "the gate must reject a Tier-2 node with no wallet bound");
        exec.Error.Should().Be(ChainCapabilityGate.NoWalletBoundMessage);

        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never, "HandleAsync must be SKIPPED — no chain broadcast on a gated node");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 1b (legacy seam) — same rejection through QuestManager.ExecuteAsync
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LegacySeam_Tier2_NoWalletBound_FailsClosed_NoBroadcast()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, WalletManager = WalletManagerMocks.Empty() };
        var quest = BuildLinearQuest(h, QuestNodeType.Transfer, "Transfer");
        var transferNode = quest.Nodes[0];

        var manager = h.NewManager();
        var run = await manager.ExecuteAsync(quest.Id, AvatarId);
        run.IsError.Should().BeFalse(run.Message);

        var exec = h.ExecutionFor(run.Result!.Id, transferNode.Id);
        exec.State.Should().Be(QuestNodeState.Failed);
        exec.Error.Should().Be(ChainCapabilityGate.NoWalletBoundMessage);

        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 2 — Pure-metadata quest runs end-to-end with NO wallet, NO chain call
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PureMetadataQuest_NoWallet_RunsEndToEnd_NoChainCall()
    {
        // A Tier-1-only DAG (capability flag false everywhere). The wallet
        // manager is empty AND its QueryAsync must NEVER be consulted, because
        // the gate is not even reached for a Tier-1 node.
        var walletMock = new Mock<IWalletManager>();
        walletMock.Setup(m => m.QueryAsync(
                It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { IsError = false, Result = Array.Empty<IWallet>() });

        var node = new Tier1RecordingHandler(QuestNodeType.Condition);
        var h = new Harness { NodeHandler = node, WalletManager = walletMock.Object };
        var quest = BuildLinearQuest(h, QuestNodeType.Condition, "A", "B", "C");

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        foreach (var n in quest.Nodes)
            node.CountFor(n.Id).Should().Be(1, $"Tier-1 node {n.Name} runs unconditionally");
        h.RunStore.GetByIdAsync(runId).GetAwaiter().GetResult().Result!
            .Status.Should().Be(QuestRunStatus.Succeeded);

        // The gate is never triggered for Tier-1 ⇒ the wallet manager is never
        // even queried (no capability resolution, no chain manager touched).
        walletMock.Verify(m => m.QueryAsync(
                It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()),
            Times.Never, "a pure-metadata quest never resolves wallet-bound capability");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CASE 3 — Tier-2 ALLOWED when a wallet IS bound (gate passes → HandleAsync)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DurableSeam_Tier2_WalletBound_GatePasses_HandleInvoked()
    {
        var (handler, nft) = NewTransferHandler(succeed: true);
        var h = new Harness { NodeHandler = handler, WalletManager = WalletManagerMocks.WithOneWallet() };
        var quest = BuildLinearQuest(h, QuestNodeType.Transfer, "Transfer");
        var transferNode = quest.Nodes[0];

        var manager = h.NewManager();
        var start = await manager.StartWorkflowRunAsync(quest.Id, AvatarId);
        start.IsError.Should().BeFalse(start.Message);
        var runId = start.Result!.Id;

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        var exec = h.ExecutionFor(runId, transferNode.Id);
        exec.State.Should().Be(QuestNodeState.Succeeded, "the gate passes when a wallet is bound");

        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once, "HandleAsync ran and invoked the chain manager exactly once");
    }
}
