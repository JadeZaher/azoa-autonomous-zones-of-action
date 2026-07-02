using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
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
/// T10 — composed-DAG determinism (economic-primitive-nodes).
///
/// <para>Builds real multi-node DAGs that mix Tier-2 chain actions
/// (Swap/Grant/Refund) with the Tier-1 <see cref="QuestNodeType.GateCheck"/>
/// branch primitive, runs them through the LEGACY synchronous
/// <see cref="QuestManager.ExecuteAsync"/> executor (the simplest deterministic
/// driver for a multi-node graph — the saga path is single-successor
/// self-advancing), and asserts the gate's Pass/Fail deterministically drives
/// the downstream run/skip pattern.</para>
///
/// <para><b>Gate→downstream skip semantics</b> (verified against
/// <c>QuestManager.cs:262-289</c>): a node is SKIPPED when an incoming
/// <see cref="QuestEdgeType.Control"/> edge's source execution is
/// <see cref="QuestNodeState.Failed"/>. <see cref="GateCheckNodeHandler"/>
/// returns <c>Fail</c> when its predicate is not met, so a <c>Control</c> edge
/// <c>gate → downstream</c> skips the downstream branch on gate-Fail and runs
/// it on gate-Pass. The gate's Pass/Fail is itself driven by the upstream Swap
/// node's serialized output (<c>upstream.Swap.Result.PriceImpact</c>), so the
/// whole run is a pure function of the mocked manager outputs.</para>
///
/// <para>A wallet IS bound (the wallet manager returns one wallet) so the
/// Tier-2 nodes pass the D1 chain-capability gate and the test exercises branch
/// routing rather than the capability rejection (covered by
/// <c>ChainCapabilityGateTests</c>).</para>
/// </summary>
public class ComposedDagDeterminismTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();

    // ── handler/manager mocks ────────────────────────────────────────────────

    private static (SwapNodeHandler Handler, Mock<ISwapManager> Mgr) NewSwap(double priceImpact)
    {
        var mgr = new Mock<ISwapManager>();
        mgr.Setup(m => m.GetSwapTransactionAsync(It.IsAny<SwapExecuteRequest>(), It.IsAny<string?>()))
           .ReturnsAsync(new AZOAResult<SwapQuoteResponse>
           {
               // PriceImpact is the deterministic driver for the downstream gate.
               Result = new SwapQuoteResponse { PriceImpact = priceImpact },
           });
        return (new SwapNodeHandler(mgr.Object), mgr);
    }

    private static (GrantNodeHandler Handler, Mock<INftManager> Nft) NewGrant()
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });
        return (new GrantNodeHandler(nft.Object, new Mock<IHolonManager>().Object), nft);
    }

    private static (RefundNodeHandler Handler, Mock<INftManager> Nft) NewRefund(Guid nftId)
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.GetAsync(nftId, It.IsAny<AZOARequest?>()))
           .ReturnsAsync(new AZOAResult<INft> { Result = new Holon { Id = nftId, AssetType = "NFT" } });
        nft.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });
        return (new RefundNodeHandler(nft.Object), nft);
    }

    // The gate reads the upstream Swap node's serialized AZOAResult<SwapQuoteResponse>.
    // QuestNodeJson.Options applies NO naming policy ⇒ PascalCase members; the
    // evaluator navigates members case-sensitively, hence "Result.PriceImpact".
    private const string GatePredicate = "upstream.Swap.Result.PriceImpact < 0.5";

    private static QuestNode Node(QuestNodeType type, string name, string config, bool entry = false, bool terminal = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            NodeType = type,
            Config = config,
            IsEntry = entry,
            IsTerminal = terminal,
        };

    private static QuestEdge Edge(QuestNode from, QuestNode to) =>
        new() { Id = Guid.NewGuid(), SourceNodeId = from.Id, TargetNodeId = to.Id, EdgeType = QuestEdgeType.Control };

    private static string SwapConfig() => JsonSerializer.Serialize(new SwapNodeConfig { Request = new SwapExecuteRequest() });
    private static string GrantConfig() => JsonSerializer.Serialize(new GrantNodeConfig { Request = new NftMintRequest() });
    private static string GateConfig() => JsonSerializer.Serialize(new GateCheckNodeConfig { Predicate = GatePredicate });
    private static string RefundConfig(Guid nftId) =>
        JsonSerializer.Serialize(new RefundNodeConfig { NftId = nftId, Request = new NftTransferRequest() });

    // ── DAG 1: swap → gate → grant ───────────────────────────────────────────

    [Theory]
    [InlineData(0.1, true)]   // low price impact ⇒ gate Pass ⇒ grant RUNS
    [InlineData(0.9, false)]  // high price impact ⇒ gate Fail ⇒ grant SKIPPED
    public async Task SwapGateGrant_GatePassRunsGrant_GateFailSkipsGrant(double priceImpact, bool expectGrantRuns)
    {
        var (swap, _) = NewSwap(priceImpact);
        var (grant, nft) = NewGrant();
        var gate = new GateCheckNodeHandler(HolonManagerMocks.Empty());

        var swapNode = Node(QuestNodeType.Swap, "Swap", SwapConfig(), entry: true);
        var gateNode = Node(QuestNodeType.GateCheck, "Gate", GateConfig());
        var grantNode = Node(QuestNodeType.Grant, "Grant", GrantConfig(), terminal: true);

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "swap-gate-grant",
            AvatarId = AvatarId,
            Status = QuestStatus.Active,   // B6: must be Active to execute
            Nodes = new List<QuestNode> { swapNode, gateNode, grantNode },
            Edges = new List<QuestEdge> { Edge(swapNode, gateNode), Edge(gateNode, grantNode) },
        };

        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);
        var execStore = new InMemoryQuestNodeExecutionStore();
        var manager = new QuestManager(
            store, new InMemoryQuestRunStore(), execStore,
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { swap, gate, grant }),
            new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteAsync(quest.Id, AvatarId);
        result.IsError.Should().BeFalse(result.Message);
        var runId = result.Result!.Id;

        QuestNodeState State(QuestNode n) =>
            execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State;

        // Swap always runs and succeeds; the gate always evaluates.
        State(swapNode).Should().Be(QuestNodeState.Succeeded);

        if (expectGrantRuns)
        {
            State(gateNode).Should().Be(QuestNodeState.Succeeded, "gate predicate met ⇒ Pass");
            State(grantNode).Should().Be(QuestNodeState.Succeeded, "gate Pass ⇒ grant runs");
            nft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), AvatarId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
                Times.Once, "gate Pass ⇒ the grant's chain mint is invoked exactly once");
        }
        else
        {
            State(gateNode).Should().Be(QuestNodeState.Failed, "gate predicate not met ⇒ Fail");
            State(grantNode).Should().Be(QuestNodeState.Skipped,
                "gate Fail ⇒ the Control edge skips grant (QuestManager.cs:262-289)");
            nft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
                Times.Never, "gate Fail ⇒ grant is skipped ⇒ no chain broadcast");
        }
    }

    [Fact]
    public void SwapGateGrant_IsDeterministic_SameInputsSameBranch()
    {
        // Run the gate-Fail DAG twice with identical inputs; the run/skip pattern
        // must be byte-for-byte identical (no nondeterministic ordering/timing).
        static (string gate, string grant) RunOnce()
        {
            var (swap, _) = NewSwap(0.9);
            var (grant, _) = NewGrant();
            var gate = new GateCheckNodeHandler(HolonManagerMocks.Empty());

            var swapNode = Node(QuestNodeType.Swap, "Swap", SwapConfig(), entry: true);
            var gateNode = Node(QuestNodeType.GateCheck, "Gate", GateConfig());
            var grantNode = Node(QuestNodeType.Grant, "Grant", GrantConfig(), terminal: true);

            var quest = new QuestEntity
            {
                Id = Guid.NewGuid(), Name = "determinism", AvatarId = AvatarId,
                Status = QuestStatus.Active,   // B6: must be Active to execute
                Nodes = new List<QuestNode> { swapNode, gateNode, grantNode },
                Edges = new List<QuestEdge> { Edge(swapNode, gateNode), Edge(gateNode, grantNode) },
            };
            var store = new InMemoryQuestStore();
            store.UpsertQuestAsync(quest).GetAwaiter().GetResult();
            var execStore = new InMemoryQuestNodeExecutionStore();
            var manager = new QuestManager(
                store, new InMemoryQuestRunStore(), execStore, new QuestDagValidator(),
                new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { swap, gate, grant }),
                new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

            var runId = manager.ExecuteAsync(quest.Id, AvatarId).GetAwaiter().GetResult().Result!.Id;
            string S(QuestNode n) =>
                execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State.ToString();
            return (S(gateNode), S(grantNode));
        }

        var first = RunOnce();
        var second = RunOnce();
        second.Should().Be(first);
        first.gate.Should().Be(nameof(QuestNodeState.Failed));
        first.grant.Should().Be(nameof(QuestNodeState.Skipped));
    }

    // ── DAG 2: gate → refund-on-fail ─────────────────────────────────────────

    [Fact]
    public async Task GateFail_RoutesToRefund_WhileSuccessPathIsSkipped()
    {
        // Engine skip semantics route AROUND a failed predecessor — there is no
        // "run-on-fail" edge. So the on-fail compensation (Refund, D7) is wired as
        // a sibling branch off the shared entry: the gate gates its OWN success
        // path (Grant), while Refund runs as the fallback. On gate-Fail the
        // success path is skipped AND the refund branch runs — the fail branch is
        // "routed" to the refund.
        var nftId = Guid.NewGuid();
        var (swap, _) = NewSwap(0.9);            // 0.9 ≥ 0.5 ⇒ gate Fail
        var (grant, grantNft) = NewGrant();
        var (refund, refundNft) = NewRefund(nftId);
        var gate = new GateCheckNodeHandler(HolonManagerMocks.Empty());

        var swapNode = Node(QuestNodeType.Swap, "Swap", SwapConfig(), entry: true);
        var gateNode = Node(QuestNodeType.GateCheck, "Gate", GateConfig());
        var grantNode = Node(QuestNodeType.Grant, "Grant", GrantConfig(), terminal: true);   // success path
        var refundNode = Node(QuestNodeType.Refund, "Refund", RefundConfig(nftId), terminal: true); // on-fail path

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "gate-refund-on-fail",
            AvatarId = AvatarId,
            Status = QuestStatus.Active,   // B6: must be Active to execute
            Nodes = new List<QuestNode> { swapNode, gateNode, grantNode, refundNode },
            Edges = new List<QuestEdge>
            {
                Edge(swapNode, gateNode),    // swap → gate
                Edge(gateNode, grantNode),   // gate → grant (success path; skipped on gate Fail)
                Edge(swapNode, refundNode),  // swap → refund (fallback; runs because swap succeeded)
            },
        };

        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);
        var execStore = new InMemoryQuestNodeExecutionStore();
        var manager = new QuestManager(
            store, new InMemoryQuestRunStore(), execStore, new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { swap, gate, grant, refund }),
            new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteAsync(quest.Id, AvatarId);
        result.IsError.Should().BeFalse(result.Message);
        var runId = result.Result!.Id;

        QuestNodeState State(QuestNode n) =>
            execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State;

        State(swapNode).Should().Be(QuestNodeState.Succeeded);
        State(gateNode).Should().Be(QuestNodeState.Failed, "0.9 ≥ 0.5 ⇒ predicate not met");
        State(grantNode).Should().Be(QuestNodeState.Skipped, "gate Fail skips the success path");
        State(refundNode).Should().Be(QuestNodeState.Succeeded, "the on-fail refund branch runs");

        grantNft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never, "the success-path mint never broadcasts on gate Fail");
        refundNft.Verify(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), AvatarId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once, "the on-fail refund executes the reverse transfer exactly once");
    }

    [Fact]
    public async Task GatePass_TakesSuccessPath_RefundBranchAlsoRuns_BothBranchesExercised()
    {
        // Counterpart to the fail case: with the gate passing, the success path
        // (Grant) runs. (The refund sibling also runs here since it only depends
        // on Swap — this DAG's point is the gate→success branch, complementing
        // the gate→fail routing above; together they exercise BOTH gate branches.)
        var nftId = Guid.NewGuid();
        var (swap, _) = NewSwap(0.1);            // 0.1 < 0.5 ⇒ gate Pass
        var (grant, grantNft) = NewGrant();
        var (refund, _) = NewRefund(nftId);
        var gate = new GateCheckNodeHandler(HolonManagerMocks.Empty());

        var swapNode = Node(QuestNodeType.Swap, "Swap", SwapConfig(), entry: true);
        var gateNode = Node(QuestNodeType.GateCheck, "Gate", GateConfig());
        var grantNode = Node(QuestNodeType.Grant, "Grant", GrantConfig(), terminal: true);
        var refundNode = Node(QuestNodeType.Refund, "Refund", RefundConfig(nftId), terminal: true);

        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(), Name = "gate-pass", AvatarId = AvatarId,
            Status = QuestStatus.Active,   // B6: must be Active to execute
            Nodes = new List<QuestNode> { swapNode, gateNode, grantNode, refundNode },
            Edges = new List<QuestEdge>
            {
                Edge(swapNode, gateNode),
                Edge(gateNode, grantNode),
                Edge(swapNode, refundNode),
            },
        };

        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);
        var execStore = new InMemoryQuestNodeExecutionStore();
        var manager = new QuestManager(
            store, new InMemoryQuestRunStore(), execStore, new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { swap, gate, grant, refund }),
            new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteAsync(quest.Id, AvatarId);
        result.IsError.Should().BeFalse(result.Message);
        var runId = result.Result!.Id;

        QuestNodeState State(QuestNode n) =>
            execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State;

        State(gateNode).Should().Be(QuestNodeState.Succeeded, "0.1 < 0.5 ⇒ predicate met");
        State(grantNode).Should().Be(QuestNodeState.Succeeded, "gate Pass ⇒ success path runs");
        grantNft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), AvatarId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once);
    }
}
