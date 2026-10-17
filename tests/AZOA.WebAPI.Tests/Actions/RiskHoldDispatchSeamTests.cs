using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// OI-28 — the account pause is a control at BOTH dispatch seams, or it is not a pause.
///
/// <para>The reversal risk flag was read by exactly three production paths (checkout begin, the
/// on-ramp credit, the bridge), because those three resolve <c>IRealValueKycGate</c>. None of the
/// ~45 action node handlers did, so a flagged payer could move the disputed units through the
/// workflow engine while the compensation ladder was still walking its rungs — and once the units
/// leave the balance, rung 2 is a stub and the reversal becomes a recorded platform loss.</para>
///
/// <para>These tests drive the same flagged payer through both seams — the legacy in-process
/// <see cref="ActionManager"/> executor and the durable <see cref="ActionNodeStepHandler"/> saga
/// step — and assert the refusal lands BEFORE <c>HandleAsync</c>, i.e. that no chain call was
/// made. The wallet is deliberately BOUND in every refusal case, so the only thing that can
/// refuse is the pause and not the rail-capability check that shares the predicate.</para>
/// </summary>
public sealed class RiskHoldDispatchSeamTests
{
    private static readonly Guid PayerId = Guid.NewGuid();

    // ── Ledger doubles ───────────────────────────────────────────────────────────

    /// <summary>A ledger that answers "this account is flagged" for every account.</summary>
    private static Mock<IFiatReversalLedger> FlaggedLedger()
    {
        var ledger = new Mock<IFiatReversalLedger>();
        ledger.Setup(l => l.HasActiveFlagAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true, "Success"));
        return ledger;
    }

    /// <summary>A ledger that answers "no flag" for every account.</summary>
    private static Mock<IFiatReversalLedger> ClearLedger()
    {
        var ledger = new Mock<IFiatReversalLedger>();
        ledger.Setup(l => l.HasActiveFlagAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(false, "Success"));
        return ledger;
    }

    /// <summary>A ledger that cannot answer: it must REFUSE, never resolve to "assume clear".</summary>
    private static Mock<IFiatReversalLedger> UnreadableLedger()
    {
        var ledger = new Mock<IFiatReversalLedger>();
        ledger.Setup(l => l.HasActiveFlagAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { IsError = true, Message = "reversal ledger unreachable" });
        return ledger;
    }

    // ── Harness (mirrors ValueRailCapabilityGateTests, plus the reversal ledger) ──

    private sealed class Harness
    {
        public InMemorySagaStore SagaStore { get; } = new();
        public InMemoryActionStore ActionStore { get; } = new();
        public InMemoryActionRunStore RunStore { get; } = new();
        public InMemoryActionNodeExecutionStore ExecutionStore { get; } = new();
        public required IActionNodeHandler NodeHandler { get; init; }
        public IWalletManager WalletManager { get; init; } = WalletManagerMocks.WithOneWallet();
        public IFiatReversalLedger? Ledger { get; init; }

        /// <summary>The legacy seam. The ledger is supplied by NAME, never by position.</summary>
        private InMemoryActionWorkflowAdmissionStore? _admission;
        private InMemoryActionWorkflowAdmissionStore Admission => _admission ??= new(RunStore, ExecutionStore, SagaStore);

        public ActionManager NewManager() => new(
            ActionStore, RunStore, ExecutionStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new[] { NodeHandler }),
            SagaStore, WalletManager,
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough(),
            accountPauseLedger: Ledger, workflowAdmission: Admission);

        /// <summary>
        /// The durable seam. The ledger is REGISTERED IN THE CONTAINER rather than handed to the
        /// constructor directly, because that is the exact mechanism production depends on: it
        /// proves the optional parameter is actually filled by DI and not merely declared.
        /// </summary>
        public (SagaProcessor Processor, ServiceProvider Scope) NewProcessor()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ISagaStore>(SagaStore);
            services.AddSingleton<IActionStore>(ActionStore);
            services.AddSingleton<IActionRunStore>(RunStore);
            services.AddSingleton<IActionNodeExecutionStore>(ExecutionStore);
            services.AddSingleton<IActionWorkflowAdmissionStore>(Admission);
            services.AddSingleton<IActionNodeHandlerRegistry>(
                new ActionNodeHandlerRegistry(new[] { NodeHandler }));
            services.AddSingleton<IWalletManager>(WalletManager);
            services.AddSingleton(BlockchainProviderFactoryFakes.Returning());
            services.AddSingleton(BindingResolverFakes.PassThrough());
            services.AddSingleton(DurableActionExecutionHarness.OwnerSigningAuthority());
            if (Ledger is not null)
                services.AddSingleton<IFiatReversalLedger>(Ledger);
            services.AddScoped<IStepHandler<ActionStepPayload>, ActionNodeStepHandler>();
            services.AddScoped<IStepHandler<ActionCompensatePayload>, ActionCompensateStepHandler>();

            var provider = services.BuildServiceProvider();
            var processor = new SagaProcessor(
                SagaStore,
                new SagaRegistry(new ISagaDefinition[] { new ActionWorkflowSagaDefinition() }),
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

        public ActionNodeExecution ExecutionFor(Guid runId, Guid nodeId) =>
            ExecutionStore.GetByRunAndNodeAsync(runId, nodeId).GetAwaiter().GetResult().Result!;
    }

    /// <summary>Tier-1 recording handler — moves no value, so the pause must not apply to it.</summary>
    private sealed class Tier1RecordingHandler : IActionNodeHandler
    {
        private readonly List<Guid> _ran = new();
        public ActionNodeType NodeType { get; }
        public Tier1RecordingHandler(ActionNodeType nodeType) => NodeType = nodeType;
        public bool RequiresChainCapability => false;
        public int CountFor(Guid nodeId) { lock (_ran) return _ran.Count(id => id == nodeId); }

        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            lock (_ran) _ran.Add(context.NodeId);
            return Task.FromResult(ActionNodeHandlerResult.Ok($"ran {context.NodeId}"));
        }
    }

    // ── Action builder (one linear chain of one node type) ────────────────────────

    private static ActionEntity BuildLinearAction(Harness h, ActionNodeType nodeType, params string[] names)
    {
        var actionId = Guid.NewGuid();
        var nodes = new List<ActionNode>();
        for (var i = 0; i < names.Length; i++)
        {
            nodes.Add(new ActionNode
            {
                Id = Guid.NewGuid(),
                ActionId = actionId,
                Name = names[i],
                NodeType = nodeType,
                Config = TransferConfigJson(),
                ExecutionOrder = i,
                IsEntry = i == 0,
                IsTerminal = i == names.Length - 1,
            });
        }
        var edges = new List<ActionEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            edges.Add(new ActionEdge
            {
                Id = Guid.NewGuid(),
                ActionId = actionId,
                SourceNodeId = nodes[i].Id,
                TargetNodeId = nodes[i + 1].Id,
                EdgeType = ActionEdgeType.Control,
            });
        }
        var action = new ActionEntity
        {
            Id = actionId,
            Name = "PausedPayerAction",
            UserId = PayerId,
            Status = ActionStatus.Active,
            Nodes = nodes,
            Edges = edges,
        };
        h.ActionStore.UpsertActionAsync(action).GetAwaiter().GetResult();
        return action;
    }

    /// <summary>A body the real TransferNodeHandler can deserialize IF the gate ever lets it through.</summary>
    private static string TransferConfigJson() =>
        "{\"NftId\":\"" + Guid.NewGuid()
        + "\",\"Request\":{\"TargetUserId\":\"" + Guid.NewGuid()
        + "\",\"WalletId\":\"" + Guid.NewGuid() + "\"}}";

    private static (TransferNodeHandler Handler, Mock<INftManager> Nft) NewTransferHandler()
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = false, Result = null });
        return (new TransferNodeHandler(nft.Object), nft);
    }

    private static void AssertNoChainCall(Mock<INftManager> nft) =>
        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never,
            "the pause must refuse BEFORE HandleAsync — a refusal after the broadcast is not a pause");

    private static void AssertChainCalledOnce(Mock<INftManager> nft) =>
        nft.Verify(m => m.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once,
            "an account with no flag is not paused and its value node must run normally");

    // ════════════════════════════════════════════════════════════════════════════
    // SEAM 1 — the legacy in-process ActionManager executor
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LegacySeam_FlaggedPayer_ValueNodeRefused_NoChainCall()
    {
        var (handler, nft) = NewTransferHandler();
        var ledger = FlaggedLedger();
        var h = new Harness { NodeHandler = handler, Ledger = ledger.Object };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");

        var run = await h.NewManager().ExecuteAsync(action.Id, PayerId, DurableActionExecutionHarness.Request(PayerId));
        run.IsError.Should().BeFalse(run.Message);
        var (legacyProcessor, legacyScope) = h.NewProcessor();
        await h.PumpAsync(legacyProcessor);
        legacyScope.Dispose();

        var exec = h.ExecutionFor(run.Result!.Id, action.Nodes[0].Id);
        exec.State.Should().Be(ActionNodeState.Failed);
        exec.Error.Should().Be(
            RiskHoldValueGate.RiskHoldMessage,
            "the wallet IS bound here, so the only thing that can refuse is the account pause");
        AssertNoChainCall(nft);
    }

    [Fact]
    public async Task LegacySeam_UnflaggedPayer_ValueNodeRuns()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, Ledger = ClearLedger().Object };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");

        var run = await h.NewManager().ExecuteAsync(action.Id, PayerId, DurableActionExecutionHarness.Request(PayerId));
        run.IsError.Should().BeFalse(run.Message);
        var (legacyProcessor, legacyScope) = h.NewProcessor();
        await h.PumpAsync(legacyProcessor);
        legacyScope.Dispose();

        h.ExecutionFor(run.Result!.Id, action.Nodes[0].Id)
            .State.Should().Be(ActionNodeState.Succeeded);
        AssertChainCalledOnce(nft);
    }

    [Fact]
    public async Task LegacySeam_UnreadableLedger_FailsClosed()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, Ledger = UnreadableLedger().Object };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");

        var run = await h.NewManager().ExecuteAsync(action.Id, PayerId, DurableActionExecutionHarness.Request(PayerId));
        run.IsError.Should().BeFalse(run.Message);
        var (legacyProcessor, legacyScope) = h.NewProcessor();
        await h.PumpAsync(legacyProcessor);
        legacyScope.Dispose();

        var exec = h.ExecutionFor(run.Result!.Id, action.Nodes[0].Id);
        exec.State.Should().Be(ActionNodeState.Failed);
        exec.Error.Should().Be(
            RiskHoldValueGate.UnreadableMessage,
            "an unreadable ledger is a refusal; assuming 'no flag' is how a control becomes decorative");
        AssertNoChainCall(nft);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // SEAM 2 — the durable ActionNodeStepHandler saga step
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DurableSeam_FlaggedPayer_ValueNodeRefused_NoChainCall()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, Ledger = FlaggedLedger().Object };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");

        var start = await h.NewManager().StartWorkflowRunAsync(action.Id, PayerId, DurableActionExecutionHarness.Request(PayerId));
        start.IsError.Should().BeFalse(start.Message);

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        var exec = h.ExecutionFor(start.Result!.Id, action.Nodes[0].Id);
        exec.State.Should().Be(ActionNodeState.Failed);
        exec.Error.Should().Be(
            RiskHoldValueGate.RiskHoldMessage,
            "the durable path must refuse a paused account exactly as the in-process path does");
        AssertNoChainCall(nft);
    }

    [Fact]
    public async Task DurableSeam_UnflaggedPayer_ValueNodeRuns()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, Ledger = ClearLedger().Object };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");

        var start = await h.NewManager().StartWorkflowRunAsync(action.Id, PayerId, DurableActionExecutionHarness.Request(PayerId));
        start.IsError.Should().BeFalse(start.Message);

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        h.ExecutionFor(start.Result!.Id, action.Nodes[0].Id)
            .State.Should().Be(ActionNodeState.Succeeded);
        AssertChainCalledOnce(nft);
    }

    [Fact]
    public async Task DurableSeam_UnreadableLedger_FailsClosed()
    {
        var (handler, nft) = NewTransferHandler();
        var h = new Harness { NodeHandler = handler, Ledger = UnreadableLedger().Object };
        var action = BuildLinearAction(h, ActionNodeType.Transfer, "Transfer");

        var start = await h.NewManager().StartWorkflowRunAsync(action.Id, PayerId, DurableActionExecutionHarness.Request(PayerId));
        start.IsError.Should().BeFalse(start.Message);

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        var exec = h.ExecutionFor(start.Result!.Id, action.Nodes[0].Id);
        exec.State.Should().Be(ActionNodeState.Failed);
        exec.Error.Should().Be(RiskHoldValueGate.UnreadableMessage);
        AssertNoChainCall(nft);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // The pause blocks VALUE, not the platform
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlaggedPayer_Tier1Node_StillRuns_AndTheLedgerIsNeverAsked()
    {
        // A paused account can still post, compose and evaluate. The pause exists to stop units
        // moving, not to lock a person out of a platform they are disputing a charge with.
        var node = new Tier1RecordingHandler(ActionNodeType.Condition);
        var ledger = FlaggedLedger();
        var h = new Harness { NodeHandler = node, Ledger = ledger.Object };
        var action = BuildLinearAction(h, ActionNodeType.Condition, "A", "B", "C");

        var start = await h.NewManager().StartWorkflowRunAsync(action.Id, PayerId, DurableActionExecutionHarness.Request(PayerId));
        start.IsError.Should().BeFalse(start.Message);

        var (processor, scope) = h.NewProcessor();
        await h.PumpAsync(processor);
        scope.Dispose();

        foreach (var n in action.Nodes)
            node.CountFor(n.Id).Should().Be(1, $"Tier-1 node {n.Name} moves no value");

        ledger.Verify(l => l.HasActiveFlagAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a pure-metadata node never reaches the admission predicate, so it never reads the flag");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // The predicate itself: order, empty case, and the two refusals
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Predicate_AsksThePauseBeforeCapability()
    {
        // A flagged payer with NO bound destination must hear about the pause, not about a rail:
        // "you have no Algorand destination" is not an answer a paused consumer can act on, and
        // the ordering is what guarantees the pause wins.
        var wallets = new Mock<IWalletManager>();
        wallets.Setup(m => m.QueryAsync(
                It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { IsError = false, Result = Array.Empty<IWallet>() });

        var gate = new ValueRailCapabilityGate(
            wallets.Object, "Algorand", destinationDirectories: null,
            accountPauseLedger: FlaggedLedger().Object);

        var verdict = await gate.AdmitValueNodeAsync(PayerId, "Algorand");

        verdict.IsAllowed.Should().BeFalse();
        verdict.RefusalMessage.Should().Be(RiskHoldValueGate.RiskHoldMessage);
        wallets.Verify(m => m.QueryAsync(
                It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()),
            Times.Never,
            "the pause short-circuits: a barred account is never asked which rail it can settle on");
    }

    [Fact]
    public async Task Predicate_ClearAccount_StillHonoursTheRailCheck()
    {
        var gate = new ValueRailCapabilityGate(
            WalletManagerMocks.Empty(), "Algorand", destinationDirectories: null,
            accountPauseLedger: ClearLedger().Object);

        var verdict = await gate.AdmitValueNodeAsync(PayerId, "Algorand");

        verdict.IsAllowed.Should().BeFalse();
        verdict.RefusalMessage.Should().Be(
            ValueRailCapabilityGate.NoDestinationMessage("Algorand"),
            "adding the pause must not displace the D1/D9 capability rule that shares the predicate");
    }

    [Fact]
    public void EmptyCase_IsResolvedInCode_NotLeftToADefault()
    {
        new ValueRailCapabilityGate(WalletManagerMocks.Empty(), "Algorand")
            .IsAccountPauseEnforced.Should().BeFalse(
                "no ledger means no reversal pipeline in the container, so no flag can exist to honour");

        new ValueRailCapabilityGate(
                WalletManagerMocks.Empty(), "Algorand", destinationDirectories: null,
                accountPauseLedger: ClearLedger().Object)
            .IsAccountPauseEnforced.Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Structural: a gate wired at ONE seam is not wired
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The behavioural tests above can only fail on the seam they drive. This one fails the moment
    /// either seam stops routing through the shared predicate — which is the exact way this control
    /// was lost the first time.
    /// </summary>
    [Theory]
    [InlineData("Services", "Actions", "Workflow", "ActionNodeStepHandler.cs")]
    public void BothDispatchSeams_RouteThroughTheAdmissionPredicate(params string[] relativePath)
    {
        var source = SourceOf(relativePath);

        source.Should().Contain("_railGate.AdmitValueNodeAsync",
            "both seams must consult the ONE predicate that carries the account pause; a seam "
            + "that calls only the capability check is a way around the pause");

        source.Should().NotContain("_railGate.HasUsableDestinationAsync",
            "reaching past the shared predicate to the capability check alone is how the pause "
            + "gets dropped from one seam without anyone noticing");
    }

    private static string SourceOf(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "azoa.sln")))
                return File.ReadAllText(Path.Combine(new[] { directory.FullName }.Concat(relativePath).ToArray()));
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }
}
