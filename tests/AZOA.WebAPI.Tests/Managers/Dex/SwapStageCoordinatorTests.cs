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
using AZOA.WebAPI.Services.Dex;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class SwapStageCoordinatorTests
{
    [Fact]
    public async Task WithdrawnServerSubmissionStillObservesButNeverReplays()
    {
        var fixture = new Fixture();
        fixture.Authorize();
        fixture.Stage.ServerSubmissionWithdrawnAt = DateTimeOffset.UtcNow;
        var observation = new SwapStageObservation("Pending", ["tx"], "genesis");
        fixture.Module.Setup(module => module.ObserveSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
            It.IsAny<CancellationToken>(), It.IsAny<SwapStageObservation?>())).ReturnsAsync(AZOAResult<SwapStageObservation>.Success(observation));
        fixture.Store.Setup(store => store.RecordObservationAsync("op", 1, "stage", 0, observation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Stage);
        await fixture.Service.RecoverAuthorizedAsync("op", fixture.Module.Object);
        fixture.Module.Verify(module => module.ObserveSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
            It.IsAny<CancellationToken>(), It.IsAny<SwapStageObservation?>()), Times.Once);
        fixture.Module.Verify(module => module.SubmitSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("ExpiredAbsent")]
    public async Task ExposedRecoveryObservesOriginalPlanWithoutSigningOrBroadcasting(string verdict)
    {
        var fixture = new Fixture();
        fixture.Operation.State = "Pending";
        fixture.Stage.State = "ExternalExposed";
        fixture.Stage.ExternalExposedAt = DateTimeOffset.UtcNow;
        var previous = new SwapStageObservation("Pending", ["tx"], "genesis", EvidenceJson: "{\"checkpoint\":1}");
        fixture.Stage.ObservationJson = JsonSerializer.Serialize(previous);
        var observation = new SwapStageObservation(verdict, ["tx"], "genesis");
        fixture.Module.Setup(module => module.ObserveExposedSwapStageAsync(It.IsAny<SwapIntent>(), fixture.Prepared.Plan,
            It.IsAny<CancellationToken>(), It.Is<SwapStageObservation?>(value => value != null && value.EvidenceJson == previous.EvidenceJson)))
            .ReturnsAsync(AZOAResult<SwapExternalStageObservation>.Success(new(observation)));
        if (verdict == "ExpiredAbsent")
            fixture.Module.Setup(module => module.ValidateExposedSwapNoEffect(It.IsAny<SwapIntent>(), fixture.Prepared.Plan, observation)).Returns(true);
        fixture.Store.Setup(store => store.RecordObservationAsync("op", 1, "stage", 0, observation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Stage);
        await fixture.Service.RecoverAuthorizedAsync("op", fixture.Module.Object);
        fixture.Store.Verify(store => store.SaveExposedCandidateAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<long>(), It.IsAny<PreparedSwapStage>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Module.Verify(module => module.SignSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<SwapStagePlan>(),
            It.IsAny<IReadOnlyList<SwapSigningAuthority>>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Module.Verify(module => module.SubmitSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Preparing")]
    public async Task FailedLiveAdmissionNeverTouchesCustody(string state)
    {
        var fixture = new Fixture();
        fixture.Operation.State = state;
        fixture.Store.Setup(store => store.AdmitCustodyAsync("op", 1, "stage", 0, fixture.Authority, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SwapStageRecord?)null);
        var result = await fixture.Service.SignAsync("op", 1, "stage", 0, fixture.Authority, fixture.Module.Object);
        result.IsError.Should().BeTrue();
        fixture.Module.Verify(module => module.SignSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<SwapStagePlan>(),
            It.IsAny<IReadOnlyList<SwapSigningAuthority>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationDuringCustodyCannotSaveOrBroadcastTheCandidate()
    {
        var fixture = new Fixture();
        fixture.Store.Setup(store => store.AdmitCustodyAsync("op", 1, "stage", 0, fixture.Authority, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapStageRecord { Version = 1 });
        fixture.Module.Setup(module => module.SignSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<SwapStagePlan>(),
                It.IsAny<IReadOnlyList<SwapSigningAuthority>>(), It.IsAny<CancellationToken>()))
            .Callback(() => fixture.Operation.State = "Cancelled")
            .ReturnsAsync(AZOAResult<PreparedSwapStage>.Success(fixture.Prepared));
        fixture.Store.Setup(store => store.SaveSignedStageAsync("op", 2, "stage", 1,
            It.IsAny<PreparedSwapStage>(), It.IsAny<CancellationToken>())).ReturnsAsync((SwapStageRecord?)null);
        var result = await fixture.Service.SignAsync("op", 1, "stage", 0, fixture.Authority, fixture.Module.Object);
        result.IsError.Should().BeTrue();
        fixture.Module.Verify(module => module.SubmitSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PreparedButUnauthorizedRecoveryNeverObservesOrSends()
    {
        var fixture = new Fixture();
        fixture.Stage.State = "Prepared";
        fixture.Stage.AuthorizedAt = null;
        await fixture.Service.RecoverAuthorizedAsync("op", fixture.Module.Object);
        fixture.Module.Verify(module => module.ObserveSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
            It.IsAny<CancellationToken>(), It.IsAny<SwapStageObservation?>()), Times.Never);
        fixture.Module.Verify(module => module.SubmitSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Confirmed", false)]
    [InlineData("ExpiredAbsent", false)]
    [InlineData("NeedsOperator", false)]
    [InlineData("Pending", true)]
    public async Task RecoveryObservesBeforeReplayingOnlyUnresolvedAuthorizedBytes(string verdict, bool send)
    {
        var fixture = new Fixture();
        fixture.Authorize();
        var order = new List<string>();
        var observation = new SwapStageObservation(verdict, ["tx"], "genesis");
        fixture.Module.Setup(module => module.ObserveSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
                It.IsAny<CancellationToken>(), It.IsAny<SwapStageObservation?>())).Callback(() => order.Add("observe"))
            .ReturnsAsync(AZOAResult<SwapStageObservation>.Success(observation));
        fixture.Store.Setup(store => store.RecordObservationAsync("op", 1, "stage", 0, observation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Stage);
        fixture.Module.Setup(module => module.SubmitSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<PreparedSwapStage>(),
                It.IsAny<CancellationToken>())).Callback(() => order.Add("submit"))
            .ReturnsAsync(AZOAResult<IReadOnlyList<string>>.Success(["tx"]));
        await fixture.Service.RecoverAuthorizedAsync("op", fixture.Module.Object);
        order.Should().Equal(send ? ["observe", "submit"] : new[] { "observe" });
        fixture.Module.Verify(module => module.SignSwapStageAsync(It.IsAny<SwapIntent>(), It.IsAny<SwapStagePlan>(),
            It.IsAny<IReadOnlyList<SwapSigningAuthority>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProviderNetworkMismatchNeverTouchesCustodyOrObservation()
    {
        var fixture = new Fixture();
        fixture.Module.SetupGet(module => module.ActiveNetwork).Returns(ChainNetwork.Mainnet);
        (await fixture.Service.SignAsync("op", 1, "stage", 0, fixture.Authority, fixture.Module.Object)).IsError.Should().BeTrue();
        fixture.Store.Verify(store => store.AdmitCustodyAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<long>(), It.IsAny<SigningAdmissionAuthority>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Fixture
    {
        public Mock<ISwapOperationStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IDexTransactionModule> Module { get; } = new(MockBehavior.Strict);
        public SwapOperationRecord Operation { get; }
        public SwapStageRecord Stage { get; }
        public PreparedSwapStage Prepared { get; }
        public SigningAdmissionAuthority Authority { get; }
        public SwapStageCoordinator Service { get; }

        public Fixture()
        {
            var actor = Guid.NewGuid();
            var owner = new DexParticipant(actor, Guid.NewGuid(), "owner");
            var terms = new DexQuoteTerms(1, actor, null, owner, owner, "owner", new("Algorand", "Devnet", "genesis"),
                new("0", "ALGO", 6, true), new("1", "ASA", 0, false), "100", "100", "100", "90", 100,
                new("InputBaseUnits", 1, "0", 0, "0", ""), new("3000", "100000", "1000"), "Tinyman", "2",
                JsonSerializer.SerializeToElement(new { fixture = true }), DateTimeOffset.UtcNow.AddMinutes(5), []);
            var intent = new SwapIntent(1, "op", "quote", "quote-hash", terms, "request-hash");
            var intentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent));
            var hash = CanonicalJson.HashCanonical(intentJson);
            var plan = new SwapStagePlan("Swap", 0, SwapOperationIdentity.Preparation("op", "Swap", 0, hash), hash,
                ["owner"], "{}", "{}", "3000", "0");
            var planJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
            Prepared = new(plan, ["tx"], "[\"signed\"]");
            Operation = new() { Id = "op", State = "Preparing", Version = 1, CurrentStageId = "stage", IntentJson = intentJson, IntentHash = hash };
            Stage = new() { Id = "stage", OperationId = "op", State = "Planned", PlanJson = planJson, PlanHash = CanonicalJson.HashCanonical(planJson),
                IntentHash = hash };
            Authority = new(1, "DirectJwt", new(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5), null, null, [], true), new(owner.WalletId, actor, "owner", "Algorand"), [AzoaScopes.SwapSign], []);
            Store.Setup(store => store.GetAsync("op", It.IsAny<CancellationToken>())).ReturnsAsync(() => Operation);
            Store.Setup(store => store.GetStageAsync("stage", It.IsAny<CancellationToken>())).ReturnsAsync(() => Stage);
            Module.SetupGet(module => module.ChainType).Returns("Algorand");
            Module.SetupGet(module => module.ActiveNetwork).Returns(ChainNetwork.Devnet);
            Module.SetupGet(module => module.DexCapability).Returns(new DexCapability("Tinyman", "2", true, false, true, true, true, []));
            Module.Setup(module => module.ValidateSignedSwapStage(It.IsAny<SwapIntent>(), It.IsAny<SwapStagePlan>(), Prepared.SignedPayloadJson))
                .Returns(AZOAResult<PreparedSwapStage>.Success(Prepared));
            Service = new(Store.Object);
        }

        public void Authorize()
        {
            Operation.State = "Pending";
            Stage.State = "SubmissionAuthorized";
            Stage.AuthorizedAt = DateTimeOffset.UtcNow;
            Stage.PreparedJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(Prepared));
            Stage.PreparedHash = CanonicalJson.HashCanonical(Stage.PreparedJson);
        }
    }
}

