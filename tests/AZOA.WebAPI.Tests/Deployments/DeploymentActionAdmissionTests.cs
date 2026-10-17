using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Deployments;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class DeploymentActionAdmissionTests
{
    [Fact]
    public async Task Restart_after_lost_admission_uses_original_quote_without_new_signature_or_market_quote()
    {
        var harness = await Harness.Create();
        harness.Store.LoseNextAdmissionResponse = true;
        await Assert.ThrowsAsync<IOException>(() => harness.Service.StartActionAsync(harness.Reference, harness.Canonical, false));
        var retry = await harness.Service.StartActionAsync(harness.Reference, harness.Canonical, false);
        retry.IsError.Should().BeFalse(retry.Message);
        harness.QuoteCalls.Should().Be(1);
        harness.Store.Quotes.Should().ContainSingle();
        harness.Store.Operations.Should().ContainSingle();
        harness.Module.Verify(module => module.PrepareNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Module.Verify(module => module.SubmitNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Overlapping_read_only_quotes_converge_on_one_durable_operation_quote()
    {
        var harness = await Harness.Create();
        var results = await Task.WhenAll(harness.Service.StartActionAsync(harness.Reference, harness.Canonical, false),
            harness.Service.StartActionAsync(harness.Reference, harness.Canonical, false));
        results.Should().OnlyContain(result => !result.IsError);
        harness.Store.Quotes.Should().ContainSingle();
        var request = JsonSerializer.Deserialize<DeploymentStartRequest>(harness.Store.Operations.Single().RequestJson)!;
        var winning = JsonSerializer.Deserialize<NativeOperationQuote>(harness.Store.Quotes.Single().QuoteJson)!;
        request.NativeIntent!.Quote.Should().Be(winning);
        request.NativeIntent.Budget.MaximumNetworkFee.Should().Be("1000");
    }

    [Fact]
    public async Task Cancelled_action_cannot_admit_or_prepare_even_with_previously_claimed_quote()
    {
        var harness = await Harness.Create();
        harness.Store.ActionActive = false;
        var result = await harness.Service.StartActionAsync(harness.Reference, harness.Canonical, true);
        result.IsError.Should().BeTrue();
        harness.Store.Operations.Should().BeEmpty();
        harness.Module.Verify(module => module.PrepareNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prepared_wire_without_submission_authority_is_never_sent_by_observer()
    {
        var harness = await Harness.Create();
        await harness.Service.StartActionAsync(harness.Reference, harness.Canonical, false);
        var row = harness.Store.Operations.Single();
        row.State = "Prepared"; row.PreparedJson = JsonSerializer.Serialize(new NativeStagePreparation("Execute", "tx", "wire", "protocol", "{}", "100", "0"));
        await harness.Store.UpdateAsync(row, row.Version);
        await harness.Service.ObserveAsync(row.Id);
        (await harness.Store.ListPendingAsync(100)).Should().BeEmpty();
        harness.Module.Verify(module => module.ObserveNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Module.Verify(module => module.SubmitNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExhaustedRecoveryBudgetParksBeforePreparationOrCustody()
    {
        var harness = await Harness.Create();
        await harness.Service.StartActionAsync(harness.Reference, harness.Canonical, false);
        var row = harness.Store.Operations.Single();
        row.RecoveryMode = "AbortCleanup";
        row.State = "AwaitingAuthorization";
        row.HistoryJson = JsonSerializer.Serialize(new[] { new NativeStageHistory(
            new("CreateBuffer", "original", "wire", "protocol", "{}", "950", "0"),
            new("Confirmed", "original", "protocol", "buffer", "{}")) });
        row = (await harness.Store.UpdateAsync(row, row.Version))!;
        harness.Recovery.Setup(module => module.GetRecoveryNextStageAsync(It.IsAny<NativeOperationIntent>(),
            It.IsAny<IReadOnlyList<NativeStageHistory>>(), "AbortCleanup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NativeNextStage>.Success(new("CloseBuffer", false,
                Disposition: "CleanupRequired", Reservation: new("100", "0", "0"))));
        var receipt = await harness.Service.ContinueAsync(row.Id, Guid.Parse(row.ActorUserId), null);
        receipt.IsError.Should().BeFalse(receipt.Message);
        receipt.Result!.State.Should().Be("RecoveryQuoteRequired");
        harness.Module.Verify(module => module.PrepareNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Module.Verify(module => module.SubmitNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<NativeStagePreparation>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReplacementQuoteReobservesFailedChildBeforeNewQuoteOrCustody()
    {
        var harness = await Harness.Create();
        await harness.Service.StartActionAsync(harness.Reference, harness.Canonical, false);
        var parent = harness.Store.Operations.Single();
        parent.State = "Failed";
        parent.HistoryJson = JsonSerializer.Serialize(new[] { new NativeStageHistory(
            new("CreateBuffer", "original", "wire", "protocol", "{}", "1", "0"), new("Confirmed", "original", "protocol")) });
        parent = (await harness.Store.UpdateAsync(parent, parent.Version))!;
        var original = JsonSerializer.Deserialize<DeploymentStartRequest>(parent.RequestJson)!;
        var childIntent = original.NativeIntent! with { Kind = "Recovery", OperationKey = "old-recovery" };
        var previousWire = new NativeStagePreparation("CloseBuffer", "previous", "old-wire", "protocol", "{}", "1", "0");
        var child = new AZOA.WebAPI.Persistence.SurrealDb.Models.DeploymentOperation
        {
            Id = "old-recovery", ParentOperationId = parent.Id, ActorUserId = parent.ActorUserId,
            TenantId = parent.TenantId, ArtifactId = parent.ArtifactId, ArtifactHash = parent.ArtifactHash,
            RecoveryMode = "AbortCleanup", RecoveryBindingHash = "old-binding", State = "Failed", Generation = 1,
            RequestJson = JsonSerializer.Serialize(original with { NativeIntent = childIntent }),
            HistoryJson = JsonSerializer.Serialize(new[] { new NativeStageHistory(previousWire, new("FailedOnChain", "previous", "protocol")) }),
        };
        (await harness.Store.ClaimRecoveryChildAsync(parent, child)).Should().NotBeNull();
        parent = (await harness.Store.GetAsync(parent.Id))!;
        harness.Module.Setup(module => module.ObserveNativeStageAsync(It.IsAny<NativeOperationIntent>(),
            It.IsAny<NativeStagePreparation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NativeStageObservation>.Success(new("Pending", "previous", "protocol")));
        var priorQuotes = harness.QuoteCalls;
        var result = await harness.Service.QuoteRecoveryAsync(parent.Id, Guid.Parse(parent.ActorUserId), null,
            new(parent.Version, "replacement", "AbortCleanup", "sender", original.NativeIntent!.Authorities, new("1000", "0", "0", "")));
        result.IsError.Should().BeTrue();
        harness.QuoteCalls.Should().Be(priorQuotes);
        harness.Module.Verify(module => module.ObserveNativeStageAsync(It.IsAny<NativeOperationIntent>(),
            It.Is<NativeStagePreparation>(wire => wire.TransactionId == "previous"), It.IsAny<CancellationToken>()), Times.Once);
        harness.Module.Verify(module => module.PrepareNativeStageAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Harness
    {
        public InMemoryDeploymentStore Store { get; } = new();
        public Mock<INativeOperationModule> Module { get; } = new();
        public Mock<INativeRecoveryModule> Recovery { get; }
        public Harness() { Recovery = Module.As<INativeRecoveryModule>(); }
        public DeploymentService Service { get; private set; } = null!;
        public ActionExternalOperationReference Reference { get; private set; } = null!;
        public string Canonical { get; private set; } = "";
        public int QuoteCalls;

        public static async Task<Harness> Create()
        {
            var harness = new Harness();
            var actor = Guid.NewGuid(); var bundleId = Guid.NewGuid(); var runId = Guid.NewGuid(); var nodeId = Guid.NewGuid();
            var bundle = new Mock<IActionGraphBundleStore>();
            bundle.Setup(store => store.GetByIdAsync(bundleId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IActionGraphBundle>.Success(new ActionGraphBundle { Id = bundleId, UserId = actor }));
            var provider = new Mock<IBlockchainProvider>();
            var factory = new Mock<IBlockchainProviderFactory>();
            factory.Setup(item => item.GetProvider("Ethereum", ChainNetwork.Devnet)).Returns(provider.Object);
            factory.Setup(item => item.TryGetModule<INativeOperationModule>(provider.Object, out It.Ref<INativeOperationModule?>.IsAny))
                .Returns((IBlockchainProvider _, out INativeOperationModule? module) => { module = harness.Module.Object; return true; });
            harness.Module.Setup(module => module.QuoteNativeOperationAsync(It.IsAny<NativeOperationIntent>(), It.IsAny<CancellationToken>()))
                .Returns(async (NativeOperationIntent _, CancellationToken _) =>
                {
                    var price = Interlocked.Increment(ref harness.QuoteCalls) * 100;
                    await Task.Yield();
                    return AZOAResult<NativeOperationQuote>.Success(new("", "", "protocol", price.ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "0", "{}", DateTimeOffset.UtcNow.AddMinutes(10)));
                });
            var access = new Mock<IValueAccessService>();
            access.Setup(service => service.RequireValueAccessAsync(actor, null, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<bool>.Success(true));
            var governance = new Mock<INodeGovernanceGuard>();
            governance.Setup(guard => guard.EnsureChainAllowedAsync("Ethereum", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(NodeGovernanceDecision.Allowed);
            harness.Service = new DeploymentService(harness.Store, bundle.Object, Mock.Of<IActionStore>(), Mock.Of<IHolonManager>(),
                new DeploymentArtifactCompiler(Mock.Of<IActionDagValidator>()), factory.Object, access.Object, governance.Object,
                Mock.Of<IFrozenActionRuntime>(), Mock.Of<IWalletStore>(), Mock.Of<ITenantConsentGate>(), Mock.Of<IEcosystemStore>(), Mock.Of<IDappSeriesStore>(), Mock.Of<IDeploymentRecoveryAuditStore>(MockBehavior.Strict));
            var native = new NativeArtifact("evm-contract/1", JsonSerializer.SerializeToElement(new EvmContractArtifact("0x6000", "0x6000")), "fixture");
            var target = new DeploymentTarget("NativeContract", "Ethereum", "Devnet");
            var payload = new DeploymentArtifactPayload(2, bundleId, actor, null, target, null, native);
            await harness.Store.AddArtifactAsync(new DeploymentArtifact { Id = "artifact", ArtifactHash = "artifact-hash", PayloadHash = "payload-hash",
                OwnerUserId = actor.ToString("N"), TenantId = "", BundleId = bundleId.ToString("N"), PayloadJson = JsonSerializer.Serialize(payload), CreatedAt = DateTimeOffset.UtcNow });
            var intent = new NativeOperationIntent(1, "", "Deploy", "Ethereum", "Devnet", "", "artifact-hash",
                JsonSerializer.SerializeToElement(new EvmNativeInput(native, "sender")),
                [new("sender", new SigningContext(actor, Guid.NewGuid(), false, GrantorUserId: actor, Scope: AzoaScopes.ContractDeploySign))], new("1000", "0", "0", ""));
            var config = new DeploymentNodeConfig { Deployment = new("artifact", "artifact-hash", target, intent) };
            var frozen = new ActionFrozenNodeInput(1, runId, nodeId, ActionNodeType.ActionGraphBundleDeploy, actor, null,
                JsonSerializer.SerializeToElement(config), new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
            harness.Canonical = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(frozen));
            var key = DeploymentActionResolver.RequestKey(runId, nodeId);
            harness.Reference = new(1, "deployment", key, DeploymentIdentity.Operation(actor, null, key), CanonicalJson.HashCanonical(harness.Canonical), actor, null);
            return harness;
        }
    }
}
