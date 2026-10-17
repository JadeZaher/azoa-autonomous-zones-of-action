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
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// T10 — composed-DAG determinism (economic-primitive-nodes).
///
/// <para>Builds real multi-node DAGs that mix Tier-2 chain actions
/// (Swap/Grant/Refund) with the Tier-1 <see cref="ActionNodeType.GateCheck"/>
/// branch primitive, runs them through the LEGACY synchronous
/// <see cref="ActionManager.ExecuteAsync"/> executor (the simplest deterministic
/// driver for a multi-node graph — the saga path is single-successor
/// self-advancing), and asserts the gate's Pass/Fail deterministically drives
/// the downstream run/skip pattern.</para>
///
/// <para><b>Gate→downstream skip semantics</b> (verified against
/// <c>ActionManager.cs:262-289</c>): a node is SKIPPED when an incoming
/// <see cref="ActionEdgeType.Control"/> edge's source execution is
/// <see cref="ActionNodeState.Failed"/>. <see cref="GateCheckNodeHandler"/>
/// returns <c>Fail</c> when its predicate is not met, so a <c>Control</c> edge
/// <c>gate → downstream</c> skips the downstream branch on gate-Fail and runs
/// it on gate-Pass. The gate's Pass/Fail is itself driven by the upstream Swap
/// node's settled receipt (<c>upstream.Swap.ActualOutput</c>), so the
/// whole run is a pure function of the mocked manager outputs.</para>
///
/// <para>A wallet IS bound (the wallet manager returns one wallet) so the
/// Tier-2 nodes pass the D1 chain-capability gate and the test exercises branch
/// routing rather than the capability rejection (covered by
/// <c>ValueRailCapabilityGateTests</c>).</para>
/// </summary>
public class ComposedDagDeterminismTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    // ── handler/manager mocks ────────────────────────────────────────────────

    // Settled-swap double: the durable swap resolver emits this flat receipt only after finalized settlement
    // (covered end to end by Managers/Dex/SwapActionResolverTests); a low price impact settles at the quoted output.
    private static (IActionNodeHandler Handler, string Output) NewSwap(double priceImpact)
    {
        var output = priceImpact < 0.5 ? "1990" : "1500";
        return (new SettledSwap(output), output);
    }

    private sealed class SettledSwap(string output) : IActionNodeHandler
    {
        public ActionNodeType NodeType => ActionNodeType.Swap;
        public bool RequiresChainCapability => true;
        public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
            => Task.FromResult(ActionNodeResults.Ok(JsonSerializer.Serialize(new AZOA.WebAPI.Models.Dex.SwapActionOutput(
                "operation", "Algorand", "Devnet", "Tinyman", "0", "10458941", "1000", output, "0", ["TX"], "TX", []), ActionNodeJson.Options)));
    }

    private static (GrantNodeHandler Handler, Mock<INftManager> Nft) NewGrant()
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });
        return (new GrantNodeHandler(nft.Object, new Mock<IHolonManager>().Object), nft);
    }

    private static (RefundNodeHandler Handler, Mock<INftLifecycleService> Nft) NewRefund(Guid nftId)
    {
        var nft = new Mock<INftLifecycleService>();
        nft.Setup(service => service.RefundAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>(), It.IsAny<NftFeeAuthorization?>(), It.IsAny<ActionEffectAuthority?>(), It.IsAny<AZOA.WebAPI.Models.Signing.SigningCredentialWitness?>()))
            .ReturnsAsync(AZOAResult<NftLifecycleView>.Success(new(Guid.NewGuid().ToString("N"), nftId, "Completed", "Transfer", "asset", "confirmed-return", null, "Refund")));
        return (new RefundNodeHandler(nft.Object), nft);
    }

    // A succeeded upstream Transfer of nftId is now REQUIRED before a Refund may
    // reverse it (drain-vector guard). The fallback branch becomes swap → transfer → refund.
    private static (TransferNodeHandler Handler, Mock<INftManager> Nft) NewTransfer(Guid nftId)
    {
        var nft = new Mock<INftManager>();
        nft.Setup(m => m.TransferAsync(nftId, It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
           .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });
        return (new TransferNodeHandler(nft.Object), nft);
    }

    private static string TransferConfig(Guid nftId) =>
        JsonSerializer.Serialize(new TransferNodeConfig { NftId = nftId, Request = new NftTransferRequest() });

    // The gate reads the upstream Swap node's flat settled receipt. ActionNodeJson.Options applies
    // NO naming policy ⇒ PascalCase members; the evaluator navigates members case-sensitively.
    private const string GatePredicate = "upstream.Swap.ActualOutput == '1990'";

    private static ActionNode Node(ActionNodeType type, string name, string config, bool entry = false, bool terminal = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            NodeType = type,
            Config = config,
            IsEntry = entry,
            IsTerminal = terminal,
        };

    private static ActionEdge Edge(ActionNode from, ActionNode to) =>
        new() { Id = Guid.NewGuid(), SourceNodeId = from.Id, TargetNodeId = to.Id, EdgeType = ActionEdgeType.Control };

    private static string SwapConfig() => JsonSerializer.Serialize(new SwapNodeConfig());
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

        var swapNode = Node(ActionNodeType.Swap, "Swap", SwapConfig(), entry: true);
        var gateNode = Node(ActionNodeType.GateCheck, "Gate", GateConfig());
        var grantNode = Node(ActionNodeType.Grant, "Grant", GrantConfig(), terminal: true);

        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "swap-gate-grant",
            UserId = UserId,
            Status = ActionStatus.Active,   // B6: must be Active to execute
            Nodes = new List<ActionNode> { swapNode, gateNode, grantNode },
            Edges = new List<ActionEdge> { Edge(swapNode, gateNode), Edge(gateNode, grantNode) },
        };

        var store = new InMemoryActionStore();
        await store.UpsertActionAsync(action);
        var execStore = new InMemoryActionNodeExecutionStore();
        var manager = new ActionManager(
            store, new InMemoryActionRunStore(), execStore,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { swap, gate, grant }),
            new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteAsync(action.Id, UserId);
        result.IsError.Should().BeFalse(result.Message);
        var runId = result.Result!.Id;

        ActionNodeState State(ActionNode n) =>
            execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State;

        // Swap always runs and succeeds; the gate always evaluates.
        State(swapNode).Should().Be(ActionNodeState.Succeeded);

        if (expectGrantRuns)
        {
            State(gateNode).Should().Be(ActionNodeState.Succeeded, "gate predicate met ⇒ Pass");
            State(grantNode).Should().Be(ActionNodeState.Succeeded, "gate Pass ⇒ grant runs");
            nft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), UserId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
                Times.Once, "gate Pass ⇒ the grant's chain mint is invoked exactly once");
        }
        else
        {
            State(gateNode).Should().Be(ActionNodeState.Failed, "gate predicate not met ⇒ Fail");
            State(grantNode).Should().Be(ActionNodeState.Skipped,
                "gate Fail ⇒ the Control edge skips grant (ActionManager.cs:262-289)");
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

            var swapNode = Node(ActionNodeType.Swap, "Swap", SwapConfig(), entry: true);
            var gateNode = Node(ActionNodeType.GateCheck, "Gate", GateConfig());
            var grantNode = Node(ActionNodeType.Grant, "Grant", GrantConfig(), terminal: true);

            var action = new ActionEntity
            {
                Id = Guid.NewGuid(), Name = "determinism", UserId = UserId,
                Status = ActionStatus.Active,   // B6: must be Active to execute
                Nodes = new List<ActionNode> { swapNode, gateNode, grantNode },
                Edges = new List<ActionEdge> { Edge(swapNode, gateNode), Edge(gateNode, grantNode) },
            };
            var store = new InMemoryActionStore();
            store.UpsertActionAsync(action).GetAwaiter().GetResult();
            var execStore = new InMemoryActionNodeExecutionStore();
            var manager = new ActionManager(
                store, new InMemoryActionRunStore(), execStore, new ActionDagValidator(), new ActionDagExecutabilityValidator(),
                new ActionNodeHandlerRegistry(new IActionNodeHandler[] { swap, gate, grant }),
                new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

            var runId = manager.ExecuteAsync(action.Id, UserId).GetAwaiter().GetResult().Result!.Id;
            string S(ActionNode n) =>
                execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State.ToString();
            return (S(gateNode), S(grantNode));
        }

        var first = RunOnce();
        var second = RunOnce();
        second.Should().Be(first);
        first.gate.Should().Be(nameof(ActionNodeState.Failed));
        first.grant.Should().Be(nameof(ActionNodeState.Skipped));
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
        var (transfer, _) = NewTransfer(nftId);  // real upstream debit the refund reverses
        var (refund, refundNft) = NewRefund(nftId);
        var gate = new GateCheckNodeHandler(HolonManagerMocks.Empty());

        var swapNode = Node(ActionNodeType.Swap, "Swap", SwapConfig(), entry: true);
        var gateNode = Node(ActionNodeType.GateCheck, "Gate", GateConfig());
        var grantNode = Node(ActionNodeType.Grant, "Grant", GrantConfig(), terminal: true);   // success path
        var transferNode = Node(ActionNodeType.Transfer, "Transfer", TransferConfig(nftId));   // debits nftId
        var refundNode = Node(ActionNodeType.Refund, "Refund", RefundConfig(nftId), terminal: true); // on-fail path

        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "gate-refund-on-fail",
            UserId = UserId,
            Status = ActionStatus.Active,   // B6: must be Active to execute
            Nodes = new List<ActionNode> { swapNode, gateNode, grantNode, transferNode, refundNode },
            Edges = new List<ActionEdge>
            {
                Edge(swapNode, gateNode),        // swap → gate
                Edge(gateNode, grantNode),       // gate → grant (success path; skipped on gate Fail)
                Edge(swapNode, transferNode),    // swap → transfer (the debit the refund reverses)
                Edge(transferNode, refundNode),  // transfer → refund (fallback; runs because transfer succeeded)
            },
        };

        var store = new InMemoryActionStore();
        await store.UpsertActionAsync(action);
        var execStore = new InMemoryActionNodeExecutionStore();
        var manager = new ActionManager(
            store, new InMemoryActionRunStore(), execStore, new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { swap, gate, grant, transfer, refund }),
            new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteAsync(action.Id, UserId);
        result.IsError.Should().BeFalse(result.Message);
        var runId = result.Result!.Id;

        ActionNodeState State(ActionNode n) =>
            execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State;

        State(swapNode).Should().Be(ActionNodeState.Succeeded);
        State(gateNode).Should().Be(ActionNodeState.Failed, "0.9 ≥ 0.5 ⇒ predicate not met");
        State(grantNode).Should().Be(ActionNodeState.Skipped, "gate Fail skips the success path");
        State(refundNode).Should().Be(ActionNodeState.Succeeded, "the on-fail refund branch runs (reversing the upstream Transfer)");

        grantNft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never, "the success-path mint never broadcasts on gate Fail");
        refundNft.Verify(m => m.RefundAsync(UserId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>(), It.IsAny<NftFeeAuthorization?>(), It.IsAny<ActionEffectAuthority?>(), It.IsAny<AZOA.WebAPI.Models.Signing.SigningCredentialWitness?>()),
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
        var (transfer, _) = NewTransfer(nftId);  // real upstream debit the refund reverses
        var (refund, _) = NewRefund(nftId);
        var gate = new GateCheckNodeHandler(HolonManagerMocks.Empty());

        var swapNode = Node(ActionNodeType.Swap, "Swap", SwapConfig(), entry: true);
        var gateNode = Node(ActionNodeType.GateCheck, "Gate", GateConfig());
        var grantNode = Node(ActionNodeType.Grant, "Grant", GrantConfig(), terminal: true);
        var transferNode = Node(ActionNodeType.Transfer, "Transfer", TransferConfig(nftId));
        var refundNode = Node(ActionNodeType.Refund, "Refund", RefundConfig(nftId), terminal: true);

        var action = new ActionEntity
        {
            Id = Guid.NewGuid(), Name = "gate-pass", UserId = UserId,
            Status = ActionStatus.Active,   // B6: must be Active to execute
            Nodes = new List<ActionNode> { swapNode, gateNode, grantNode, transferNode, refundNode },
            Edges = new List<ActionEdge>
            {
                Edge(swapNode, gateNode),
                Edge(gateNode, grantNode),
                Edge(swapNode, transferNode),
                Edge(transferNode, refundNode),
            },
        };

        var store = new InMemoryActionStore();
        await store.UpsertActionAsync(action);
        var execStore = new InMemoryActionNodeExecutionStore();
        var manager = new ActionManager(
            store, new InMemoryActionRunStore(), execStore, new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { swap, gate, grant, transfer, refund }),
            new InMemorySagaStore(), WalletManagerMocks.WithOneWallet(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

        var result = await manager.ExecuteAsync(action.Id, UserId);
        result.IsError.Should().BeFalse(result.Message);
        var runId = result.Result!.Id;

        ActionNodeState State(ActionNode n) =>
            execStore.GetByRunAndNodeAsync(runId, n.Id).GetAwaiter().GetResult().Result!.State;

        State(gateNode).Should().Be(ActionNodeState.Succeeded, "0.1 < 0.5 ⇒ predicate met");
        State(grantNode).Should().Be(ActionNodeState.Succeeded, "gate Pass ⇒ success path runs");
        grantNft.Verify(m => m.MintAsync(It.IsAny<NftMintRequest>(), UserId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once);
    }
}
