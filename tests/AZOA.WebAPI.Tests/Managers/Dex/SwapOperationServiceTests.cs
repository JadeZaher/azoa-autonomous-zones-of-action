using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Dex;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class SwapOperationServiceTests
{
    [Fact]
    public async Task ExposureResponseLossReplaysOnlyOriginalWireForOriginalParticipant()
    {
        var fixture = new Fixture();
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request)).IsError.Should().BeFalse();
        var operation = fixture.Saved!;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = new SwapStagePlan("Swap", 0, "original-preparation", operation.IntentHash,
            [intent.Terms.Owner.Address], "{\"unsigned\":\"original\"}", "{\"privateRecovery\":true}", "100", "0");
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
        var stage = new SwapStageRecord { Id = new string('a', 64), OperationId = operation.Id,
            IntentHash = operation.IntentHash, PlanJson = json, PlanHash = CanonicalJson.HashCanonical(json),
            ExternalExposedAt = DateTimeOffset.UtcNow, ExternalExposureHash = new string('e', 64), Version = 5 };
        operation.CurrentStageId = stage.Id;
        operation.State = "Pending";
        operation.Version = 8;
        fixture.Operations.Setup(store => store.GetStageAsync(stage.Id, It.IsAny<CancellationToken>())).ReturnsAsync(stage);
        fixture.Wallets.Invocations.Clear();
        var command = new SwapExposureCommand(0, stage.Id, 0, true);
        var replay = await fixture.Service.ExposeAsync(operation.Id, command, fixture.Credential);
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.UnsignedPayloadJson.Should().Be(plan.UnsignedPayloadJson);
        replay.Result.OperationVersion.Should().Be(8);
        JsonSerializer.Serialize(replay.Result).Should().NotContain("privateRecovery");
        fixture.Wallets.VerifyNoOtherCalls();
        (await fixture.Service.ExposeAsync(operation.Id, command with { AcceptPotentialExternalSubmission = false }, fixture.Credential)).IsError.Should().BeTrue();
        (await fixture.Service.ExposeAsync(operation.Id, command,
            fixture.Credential with { ActorUserId = Guid.NewGuid() })).IsError.Should().BeTrue();
        (await fixture.Service.ExposeAsync(operation.Id, command,
            fixture.Credential with { TenantId = Guid.NewGuid() })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ResponseLossRetryReturnsSameOperationWithoutRefreshingQuoteOrSigningAuthority()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.StartAsync(fixture.Credential, fixture.Request);
        first.IsError.Should().BeFalse(first.Message);
        fixture.Quotes.Invocations.Clear(); fixture.Wallets.Invocations.Clear();
        fixture.Saved!.State = "Pending";
        var replay = await fixture.Service.StartAsync(fixture.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, fixture.Request);
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.OperationId.Should().Be(first.Result!.OperationId);
        replay.Result.State.Should().Be("Pending");
        fixture.Quotes.VerifyNoOtherCalls(); fixture.Wallets.VerifyNoOtherCalls();
        fixture.Operations.Verify(store => store.AdmitAsync(It.IsAny<SwapOperationRecord>(), It.IsAny<SigningAdmissionAuthority>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangedQuoteOrActorCannotReplayAnotherImmutableRequest()
    {
        var fixture = new Fixture();
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request)).IsError.Should().BeFalse();
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request with { QuoteHash = new string('f', 64) })).IsError.Should().BeTrue();
        (await fixture.Service.StartAsync(fixture.Credential with { ActorUserId = Guid.NewGuid() }, fixture.Request)).IsError.Should().BeTrue();
        (await fixture.Service.GetAsync(fixture.Saved!.Id, fixture.Credential with { ActorUserId = Guid.NewGuid() })).IsError.Should().BeTrue();
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request with { AcceptEconomicEffects = false })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task AdmissionCarriesOnlyStoredQuoteEconomicsAndCanonicalScopeKey()
    {
        var fixture = new Fixture();
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request)).IsError.Should().BeFalse();
        var saved = fixture.Saved!;
        var intent = JsonSerializer.Deserialize<SwapIntent>(saved.IntentJson)!;
        CanonicalJson.Hash(intent.Terms).Should().Be(fixture.Quote.QuoteHash);
        saved.Id.Should().Be(SwapOperationIdentity.Operation(fixture.Credential.ActorUserId, null,
            CanonicalJson.Hash(new { Domain = "swap-request-key/1", fixture.Request.RequestKey })));
        saved.IntentHash.Should().Be(CanonicalJson.HashCanonical(saved.IntentJson));
        saved.ActionRunId.Should().BeNull(); saved.CurrentStageId.Should().BeNull();
        saved.SettlementReceiptJson.Should().BeNull();
    }

    [Fact]
    public async Task CleanupReplayRetainsExactNewConsentWithoutReactivatingTheOriginalWorkflow()
    {
        var fixture = new Fixture();
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request)).IsError.Should().BeFalse();
        var saved = fixture.Saved!;
        var intent = JsonSerializer.Deserialize<SwapIntent>(saved.IntentJson)!;
        saved.ActionRunId = Guid.NewGuid().ToString("N");
        saved.ActionNodeId = Guid.NewGuid().ToString("N");
        saved.ActionIntentHash = new string('e', 64);
        saved.State = "CleanupReady";
        saved.CleanupStarted = true;
        var request = new SwapCleanupRequest(0, "cleanup-original", intent.Terms.OutputAsset.Id, "1000", true);
        var consent = new SwapCleanupConsent(1, saved.Id, saved.QuoteHash,
            CanonicalJson.Hash(new { Domain = "swap-cleanup-request/1", request.RequestKey }), fixture.Credential.ActorUserId,
            null, intent.Terms.Owner, request.AssetId, "CloseCurrentZeroHolding", request.MaximumNetworkFee, true);
        saved.CleanupConsentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent));
        saved.Version = 7;
        fixture.Wallets.Invocations.Clear();
        var replay = await fixture.Service.BeginCleanupAsync(saved.Id, request, fixture.Credential);
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.Version.Should().Be(7);
        replay.Result.Accounting!.Complete.Should().BeFalse();
        fixture.Wallets.VerifyNoOtherCalls();
        (await fixture.Service.BeginCleanupAsync(saved.Id, request with { MaximumNetworkFee = "999" }, fixture.Credential)).IsError.Should().BeTrue();
        (await fixture.Service.BeginCleanupAsync(saved.Id, request with { AcceptCurrentZeroHoldingReplacement = false }, fixture.Credential)).IsError.Should().BeTrue();
        (await fixture.Service.BeginCleanupAsync(saved.Id, request, fixture.Credential with { ActorUserId = Guid.NewGuid() })).IsError.Should().BeTrue();
        fixture.Operations.Verify(store => store.BeginCleanupAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<SwapCleanupConsent>(),
            It.IsAny<SigningAdmissionAuthority>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task SwapReadsCarryTheSwapSigningCeiling(bool ownerAuthority, bool swapScope, bool visible)
    {
        var fixture = new Fixture();
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request)).IsError.Should().BeFalse();
        var reader = fixture.Credential with
        {
            OwnerAuthority = ownerAuthority, Scopes = swapScope ? [AzoaScopes.SwapSign] : ["wallet:read"], Kind = ownerAuthority ? "Jwt" : "ApiKey",
        };

        var read = await fixture.Service.GetAsync(fixture.Saved!.Id, reader);

        read.IsError.Should().Be(!visible, "a narrower credential sees the same not-found as another participant");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CleanupAdmissionRefusesASemanticTheOriginalModuleDoesNotOffer(bool closeZeroHolding, bool offered)
    {
        var module = new Mock<IDexTransactionModule>();
        module.SetupGet(item => item.ChainType).Returns("Ethereum");
        module.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Devnet);
        module.SetupGet(item => item.DexCapability).Returns(new DexCapability("fixture", "1", true, false, true, true, true, [],
            [SwapCleanupContract.RevokeCurrentAllowance]));
        var fixture = new Fixture(module);
        (await fixture.Service.StartAsync(fixture.Credential, fixture.Request)).IsError.Should().BeFalse();
        var saved = fixture.Saved!;
        var intent = JsonSerializer.Deserialize<SwapIntent>(saved.IntentJson)!;
        var request = new SwapCleanupRequest(saved.Version, "cleanup-semantic", closeZeroHolding ? intent.Terms.OutputAsset.Id : intent.Terms.InputAsset.Id,
            "1000", closeZeroHolding, !closeZeroHolding);
        fixture.Operations.Setup(store => store.BeginCleanupAsync(saved.Id, saved.Version, It.IsAny<SwapCleanupConsent>(),
            It.IsAny<SigningAdmissionAuthority>(), It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        fixture.Wallets.Invocations.Clear();

        var begun = await fixture.Service.BeginCleanupAsync(saved.Id, request, fixture.Credential);

        fixture.Operations.Verify(store => store.BeginCleanupAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<SwapCleanupConsent>(),
            It.IsAny<SigningAdmissionAuthority>(), It.IsAny<CancellationToken>()), offered ? Times.Once() : Times.Never());
        if (offered) return;
        begun.IsError.Should().BeTrue();
        begun.Message.Should().Contain("does not offer this cleanup semantic");
        fixture.Wallets.VerifyNoOtherCalls();
    }

    private sealed class Fixture
    {
        private delegate bool TryRail(string railId, RailEnvironment environment, out IValueRailProvider? provider);
        private delegate bool TryModule(IBlockchainProvider provider, out IDexTransactionModule? module);

        internal readonly Mock<ISwapOperationStore> Operations = new(MockBehavior.Strict);
        internal readonly Mock<IDexQuoteStore> Quotes = new(MockBehavior.Strict);
        internal readonly Mock<IWalletStore> Wallets = new(MockBehavior.Strict);
        internal readonly SigningCredentialWitness Credential;
        internal readonly SwapAdmissionRequest Request;
        internal readonly DexQuoteRecord Quote;
        internal readonly SwapOperationService Service;
        internal SwapOperationRecord? Saved;

        internal Fixture(Mock<IDexTransactionModule>? module = null)
        {
            var actor = Guid.NewGuid();
            var wallet = new AZOA.WebAPI.Models.Wallet { Id = Guid.NewGuid(), UserId = actor, ChainType = "Ethereum",
                Address = "0x1111111111111111111111111111111111111111" };
            Credential = new(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
                null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin);
            var owner = new DexParticipant(actor, wallet.Id, wallet.Address);
            var terms = new DexQuoteTerms(1, actor, null, owner, owner, owner.Address, new("Ethereum", "Devnet", "eip155:31337"),
                new("in", "IN", 18, false), new("out", "OUT", 18, false), "1000", "1000", "100", "99", 100,
                new(NodeFeeSchedule.InputBaseUnitsV1, 4, "0", 0, "0", ""), new("1000", "0", "1000"), "fixture", "1",
                JsonSerializer.SerializeToElement(new { proof = "fixture" }), DateTimeOffset.UtcNow.AddMinutes(1), []);
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms));
            Quote = new() { Id = Guid.NewGuid().ToString("N"), ActorUserId = actor.ToString("N"), TenantId = "",
                QuoteHash = CanonicalJson.HashCanonical(json), QuoteJson = json, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = terms.ExpiresAt };
            Request = new(Quote.Id, Quote.QuoteHash, "original-request", true);
            Quotes.Setup(store => store.GetAsync(Quote.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Quote);
            Wallets.Setup(store => store.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(wallet));
            Operations.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Saved);
            Operations.Setup(store => store.AdmitAsync(It.IsAny<SwapOperationRecord>(), It.IsAny<SigningAdmissionAuthority>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SwapOperationRecord candidate, SigningAdmissionAuthority _, CancellationToken _) => Saved = candidate);
            var authority = new SigningAdmissionAuthorityFactory(new Mock<IUserStore>(MockBehavior.Strict).Object, Wallets.Object,
                new Mock<ISurrealExecutor>(MockBehavior.Strict).Object);
            var factory = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
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
            var modules = new DexModuleResolver(factory.Object);
            var stages = new SwapStageCoordinator(Operations.Object);
            Service = new(Operations.Object, Quotes.Object, authority, modules, stages, new(Operations.Object, modules, stages));
        }
    }
}
