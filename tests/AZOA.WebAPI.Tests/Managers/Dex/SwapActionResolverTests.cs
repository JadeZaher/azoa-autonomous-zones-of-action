using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Dex;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using SurrealForge.Client;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>Workflow Swap nodes reach successors only through the confirmed receipt of their own frozen swap.</summary>
public sealed class SwapActionResolverTests
{
    [Fact]
    public async Task Successor_output_is_projected_only_from_the_confirmed_settlement_receipt()
    {
        var fixture = new Fixture();
        fixture.Admit(state: "Completed", settled: true);

        var observed = await fixture.Resolver.ObserveAsync(fixture.Reference, fixture.FrozenInput);

        observed.Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        observed.TransactionId.Should().Be("0xwinner");
        using var output = JsonDocument.Parse(observed.Output!);
        output.RootElement.GetProperty("ActualOutput").GetString().Should().Be("1995");
        output.RootElement.GetProperty("ActualInput").GetString().Should().Be("1010");
        output.RootElement.GetProperty("TransactionIds").EnumerateArray().Select(id => id.GetString()).Should().Equal("0xoriginal-lost", "0xwinner");
        output.RootElement.TryGetProperty("UnsignedPayloadJson", out _).Should().BeFalse("successors never see executable wire");
    }

    [Theory]
    [InlineData("Cancelled", false, ActionExternalOperationStatus.Cancelled)]
    [InlineData("NeedsOperator", false, ActionExternalOperationStatus.Ambiguous)]
    [InlineData("CleanupReady", true, ActionExternalOperationStatus.Failed)]
    public async Task Unsettled_terminal_journal_states_never_become_node_success(string state, bool cleanupStarted, ActionExternalOperationStatus expected)
    {
        var fixture = new Fixture();
        fixture.Admit(state, settled: false, cleanupStarted);

        var observed = await fixture.Resolver.ObserveAsync(fixture.Reference, fixture.FrozenInput);

        observed.Status.Should().Be(expected);
        observed.Output.Should().BeNull();
    }

    [Fact]
    public async Task Unenabled_network_fails_the_node_before_any_quote_or_admission()
    {
        var fixture = new Fixture();

        var observed = await fixture.Resolver.ObserveAsync(fixture.Reference, fixture.FrozenInput);

        observed.Status.Should().Be(ActionExternalOperationStatus.Failed);
        fixture.Quotes.VerifyNoOtherCalls();
        fixture.Operations.Verify(store => store.AdmitAsync(It.IsAny<SwapOperationRecord>(), It.IsAny<SigningAdmissionAuthority>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("request-key")]
    [InlineData("frozen-terms")]
    [InlineData("witness")]
    public async Task Envelope_or_frozen_request_drift_is_ambiguous_and_never_quotes_or_signs(string drift)
    {
        var fixture = new Fixture();
        fixture.Admit("Admitted", settled: false);
        var reference = fixture.Reference;
        switch (drift)
        {
            case "request-key": reference = reference with { RequestKey = "action:other:node:swap" }; break;
            case "frozen-terms": fixture.Operation!.IntentJson = fixture.IntentJson(grossInput: "2020"); break;
            default: fixture.Execution.SigningAuthorityJson = fixture.Witness(Guid.NewGuid()); break;
        }

        var observed = await fixture.Resolver.ObserveAsync(reference, fixture.FrozenInput);

        observed.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        fixture.Quotes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unsettleable_original_fails_the_node_and_withdraws_the_unsent_swap_instead_of_waiting()
    {
        var module = new Mock<IDexTransactionModule>(MockBehavior.Strict);
        module.SetupGet(item => item.ChainType).Returns("Ethereum");
        module.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Devnet);
        module.SetupGet(item => item.DexCapability).Returns(new DexCapability("UniswapV3", "classic-exact-input/1", true, false, true, true, true, []));
        module.Setup(item => item.GetNextSwapStageAsync(It.IsAny<SwapIntent>(), It.Is<IReadOnlyList<SwapStageHistory>>(history => history.Count == 0),
            It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<SwapNextStage>.Success(new("Swap", false, [])));
        module.Setup(item => item.PlanSwapStageAsync(It.IsAny<SwapIntent>(), "Swap", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<SwapStagePlan>.FailureWithCode("The executor authorization deadline elapsed; the original swap can no longer settle.",
                DexResultCodes.Unsettleable));
        var fixture = new Fixture(module);
        fixture.Admit("Admitted", settled: false);

        var observed = await fixture.Resolver.ObserveAsync(fixture.Reference, fixture.FrozenInput);

        observed.Status.Should().Be(ActionExternalOperationStatus.Failed, "an elapsed deadline can never settle, so the node must not stay Pending");
        fixture.Operations.Verify(store => store.CancelBeforePreparationAsync(It.Is<SwapOperationRecord>(row => row.Id == fixture.Reference.OperationId),
            It.IsAny<SigningConsentWitness>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Operations.Verify(store => store.ClaimInitialStageAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<SwapStagePlan>(),
            It.IsAny<SigningAdmissionAuthority>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_ordinary_planning_refusal_keeps_the_node_pending()
    {
        var module = new Mock<IDexTransactionModule>(MockBehavior.Strict);
        module.SetupGet(item => item.ChainType).Returns("Ethereum");
        module.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Devnet);
        module.SetupGet(item => item.DexCapability).Returns(new DexCapability("UniswapV3", "classic-exact-input/1", true, false, true, true, true, []));
        module.Setup(item => item.GetNextSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<IReadOnlyList<SwapStageHistory>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<SwapNextStage>.FailureWithCode("Ethereum allowance state is unavailable.", AzoaErrorCodes.DependencyUnavailable));
        var fixture = new Fixture(module);
        fixture.Admit("Admitted", settled: false);

        var observed = await fixture.Resolver.ObserveAsync(fixture.Reference, fixture.FrozenInput);

        observed.Status.Should().Be(ActionExternalOperationStatus.Pending);
        fixture.Operations.Verify(store => store.CancelBeforePreparationAsync(It.IsAny<SwapOperationRecord>(),
            It.IsAny<SigningConsentWitness>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Re_observing_a_cancelled_swap_re_hands_back_an_unsent_stage_nonce_a_crash_left_reserved()
    {
        var module = new Mock<IDexTransactionModule>(MockBehavior.Strict);
        module.SetupGet(item => item.ChainType).Returns("Ethereum");
        module.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Devnet);
        module.SetupGet(item => item.DexCapability).Returns(new DexCapability("UniswapV3", "classic-exact-input/1", true, false, true, true, true, []));
        var resources = module.As<IDexRetainedResourceModule>();
        resources.Setup(item => item.ReleaseUnsentStageAsync(It.IsAny<SwapIntent>(), It.Is<SwapStagePlan>(plan => plan.Stage == "Swap"),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var fixture = new Fixture(module);
        fixture.Admit("Cancelled", settled: false);
        var stage = fixture.CancelledUnsentStage();

        var observed = await fixture.Resolver.ObserveAsync(fixture.Reference, fixture.FrozenInput);
        stage.AuthorizedAt = DateTimeOffset.UtcNow;
        await fixture.Resolver.ObserveAsync(fixture.Reference, fixture.FrozenInput);

        observed.Status.Should().Be(ActionExternalOperationStatus.Cancelled);
        resources.Verify(item => item.ReleaseUnsentStageAsync(It.IsAny<SwapIntent>(), It.IsAny<SwapStagePlan>(), It.IsAny<CancellationToken>()),
            Times.Once, "only a never-sent cancelled stage is handed back; a stage that ever had send authority keeps its nonce");
    }

    private sealed class Fixture
    {
        private delegate bool TryRail(string railId, RailEnvironment environment, out IValueRailProvider? provider);
        private delegate bool TryModule(IBlockchainProvider provider, out IDexTransactionModule? module);
        private const string OwnerAddress = "0x1111111111111111111111111111111111111111";

        internal readonly Mock<ISwapOperationStore> Operations = new();
        internal readonly Mock<IDexQuoteStore> Quotes = new(MockBehavior.Strict);
        internal readonly Mock<IActionNodeExecutionStore> Executions = new();
        internal readonly SwapActionResolver Resolver;
        internal readonly ActionExternalOperationReference Reference;
        internal readonly string FrozenInput;
        internal readonly ActionNodeExecution Execution;
        internal SwapOperationRecord? Operation;
        private readonly Guid _actor = Guid.NewGuid();
        private readonly Guid _run = Guid.NewGuid();
        private readonly Guid _node = Guid.NewGuid();
        private readonly DexQuoteRequest _request;
        private readonly SigningCredentialWitness _credential;

        internal Fixture(Mock<IDexTransactionModule>? module = null)
        {
            _credential = new(1, "Jwt", _actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), null, null, [], true);
            _request = new(Guid.NewGuid(), new("Ethereum", "Devnet", "eip155:31337"), new("0x6", "IN", 6, false), new("0x7", "OUT", 6, false),
                "1010", 50, new("10000", "0", "10000"));
            using var config = JsonDocument.Parse(JsonSerializer.Serialize(new SwapNodeConfig { Quote = _request }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var frozen = new ActionFrozenNodeInput(1, _run, _node, ActionNodeType.Swap, _actor, null, config.RootElement.Clone(),
                new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
            FrozenInput = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(frozen));
            var key = SwapActionResolver.RequestKey(_run, _node);
            Reference = new(1, "swap", key, SwapOperationIdentity.Operation(_actor, null, SwapOperationIdentity.RequestKey(key)),
                CanonicalJson.HashCanonical(FrozenInput), _actor, null);
            Execution = new() { RunId = _run, NodeId = _node, ExternalOperation = Reference, FrozenInput = FrozenInput, SigningAuthorityJson = Witness(_run) };
            Executions.Setup(store => store.GetByRunAndNodeAsync(_run, _node, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<ActionNodeExecution>.Success(Execution));
            Operations.Setup(store => store.GetAsync(Reference.OperationId!, It.IsAny<CancellationToken>())).ReturnsAsync(() => Operation);
            var wallets = new Mock<IWalletStore>(MockBehavior.Strict);
            wallets.Setup(store => store.GetByIdAsync(_request.WalletId, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(
                new AZOA.WebAPI.Models.Wallet { Id = _request.WalletId, UserId = _actor, ChainType = "Ethereum", Address = OwnerAddress }));
            var factory = new Mock<IBlockchainProviderFactory>();
            if (module is not null)
            {
                var resolved = module.Object;
                var provider = new Mock<IBlockchainProvider>();
                provider.SetupGet(item => item.ChainType).Returns("Ethereum");
                factory.Setup(item => item.TryGetRailProvider("Ethereum", It.IsAny<RailEnvironment>(), out It.Ref<IValueRailProvider?>.IsAny))
                    .Returns(new TryRail((string _, RailEnvironment environment, out IValueRailProvider? rail) =>
                    {
                        rail = environment == RailEnvironment.ForChain(ChainNetwork.Devnet) ? provider.Object : null;
                        return rail is not null;
                    }));
                factory.Setup(item => item.TryGetModule(provider.Object, out It.Ref<IDexTransactionModule?>.IsAny))
                    .Returns(new TryModule((IBlockchainProvider _, out IDexTransactionModule? found) => { found = resolved; return true; }));
            }
            var authority = new SigningAdmissionAuthorityFactory(new Mock<IUserStore>(MockBehavior.Strict).Object, wallets.Object,
                new Mock<ISurrealExecutor>(MockBehavior.Strict).Object);
            var modules = new DexModuleResolver(factory.Object);
            var stages = new SwapStageCoordinator(Operations.Object);
            var quotes = new DexQuoteService(Quotes.Object, wallets.Object, new Mock<INodeFeeScheduleManager>(MockBehavior.Strict).Object,
                authority, modules, new ConfigurationBuilder().Build());
            var swaps = new SwapOperationService(Operations.Object, Quotes.Object, authority, modules, stages, new(Operations.Object, modules, stages));
            Resolver = new(quotes, swaps, Operations.Object, Executions.Object, modules);
        }

        internal string Witness(Guid run)
            => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WorkflowSigningWitness(1, _credential,
                new ActionEffectAuthority(run, _node, _actor, null, CanonicalJson.HashCanonical(FrozenInput)),
                [AzoaScopes.ActionExecute, AzoaScopes.SwapSign], [])));

        internal string IntentJson(string grossInput = "1010")
        {
            var owner = new DexParticipant(_actor, _request.WalletId, OwnerAddress);
            var terms = new DexQuoteTerms(1, _actor, null, owner, owner, owner.Address, _request.Network, _request.InputAsset, _request.OutputAsset,
                grossInput, "1000", "2000", "1990", 50, new(NodeFeeSchedule.InputBaseUnitsV1, 1, "10", 0, "10", "0x9"), _request.Budget,
                "UniswapV3", "classic-exact-input/1", JsonSerializer.SerializeToElement(new { fixture = true }), DateTimeOffset.UtcNow, []);
            return CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new SwapIntent(1, Reference.OperationId!, "quote", CanonicalJson.Hash(terms), terms, "request")));
        }

        internal SwapStageRecord CancelledUnsentStage()
        {
            var intent = JsonSerializer.Deserialize<SwapIntent>(Operation!.IntentJson)!;
            var plan = new SwapStagePlan("Swap", 0, SwapOperationIdentity.Preparation(Reference.OperationId!, "Swap", 0, Operation.IntentHash),
                Operation.IntentHash, [intent.Terms.Owner.Address], "\"unsigned\"", "{}", "1000", "0");
            var planJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
            var stage = new SwapStageRecord
            {
                Id = "stage", OperationId = Reference.OperationId!, IntentHash = Operation.IntentHash, Stage = "Swap", State = "Cancelled",
                PlanJson = planJson, PlanHash = CanonicalJson.HashCanonical(planJson),
            };
            Operation.CurrentStageId = "stage";
            Operations.Setup(store => store.GetStageAsync("stage", It.IsAny<CancellationToken>())).ReturnsAsync(stage);
            return stage;
        }

        internal void Admit(string state, bool settled, bool cleanupStarted = false)
        {
            var intent = JsonSerializer.Deserialize<SwapIntent>(IntentJson())!;
            Operation = new SwapOperationRecord
            {
                Id = Reference.OperationId!, ActorUserId = _actor.ToString("N"), TenantId = "", State = state, CleanupStarted = cleanupStarted,
                ActionRunId = _run.ToString("N"), ActionNodeId = _node.ToString("N"), ActionIntentHash = Reference.IntentHash,
                IntentJson = IntentJson(), IntentHash = CanonicalJson.HashCanonical(IntentJson()), QuoteHash = intent.QuoteHash,
                SettlementReceiptJson = settled ? CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new SwapSettlementReceipt(
                    Reference.OperationId!, intent.QuoteHash, "stage", intent.Terms.Network, ["0xoriginal-lost", "0xwinner"], "1010", "1995", "10", []))) : null,
            };
        }
    }
}
