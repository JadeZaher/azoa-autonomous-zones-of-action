using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task MoreThanSixtyFourResolvedGenerationsRemainRecoverableAndAuditPagesContinue()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("long-history");
        authority = authority with { Credential = authority.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } };
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        for (var generation = 0; generation < 65; generation++)
        {
            var current = (await _store.GetAsync(operation.Id))!;
            var stageId = SurrealRecordGuid.BareId(stage.Id);
            stage = (await _store.AuthorizeStageAsync(operation.Id, current.Version, stageId, stage.Version, authority))!;
            current = (await _store.GetAsync(operation.Id))!;
            var absent = new SwapStageObservation("ExpiredAbsent", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
                "0", "0", "0", "0", "0", "{\"absence\":\"finalized-window-fixture\"}");
            stage = (await _store.RecordObservationAsync(operation.Id, current.Version, stageId, stage.Version, absent))!;
            current = (await _store.GetAsync(operation.Id))!;
            current.ConfirmedEffectNetworkFee.Should().Be("0");
            var next = prepared.Plan with
            {
                Generation = generation + 1,
                PreparationKey = SwapOperationIdentity.Preparation(operation.Id, "swap", generation + 1, operation.IntentHash),
            };
            stage = (await _store.ClaimNextStageAsync(operation.Id, current.Version, next, authority))!;
            prepared = prepared with { Plan = next };
            current = (await _store.GetAsync(operation.Id))!;
            stage = (await _store.SaveSignedStageAsync(operation.Id, current.Version,
                SurrealRecordGuid.BareId(stage.Id), stage.Version, prepared))!;
        }
        stage.Generation.Should().Be(65);
        var first = await _store.ReadHistoryPageAsync(operation.Id);
        var second = await _store.ReadHistoryPageAsync(operation.Id, first[^1].PreviousStageId);
        var third = await _store.ReadHistoryPageAsync(operation.Id, second[^1].PreviousStageId);
        first.Count.Should().Be(25);
        second.Count.Should().Be(25);
        third.Count.Should().Be(16);
        third[^1].PreviousStageId.Should().BeNull();
        first.Concat(second).Concat(third).Select(row => row.Id).Distinct().Count().Should().Be(66);
    }

    [Fact]
    public async Task RetryRequiresTerminalNoEffectAndChargesActualPriorGasAgainstRemainingCap()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("continuation-cost");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var next = prepared.Plan with
        {
            Generation = 1, PreparationKey = SwapOperationIdentity.Preparation(operation.Id, "swap", 1, operation.IntentHash),
            ReservedNetworkFee = "2000",
        };
        (await _store.ClaimNextStageAsync(operation.Id, 3, next, authority)).Should().BeNull();
        var failure = new SwapStageObservation("FailedOnChain", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            "0", "0", "0", "1000", "0", "{\"receipt\":\"finalized-revert-fixture\"}");
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, failure)).Should().NotBeNull();
        (await _store.ClaimNextStageAsync(operation.Id, 4, next with { ReservedNetworkFee = "2001" }, authority)).Should().BeNull();
        var saved = (await _store.ClaimNextStageAsync(operation.Id, 4, next, authority))!;
        saved.Generation.Should().Be(1);
        saved.PreviousStageId.Should().Be(stageId);
        saved.AuthorizedAt.Should().BeNull();
        (await _store.GetStageAsync(stageId))!.State.Should().Be("FailedOnChain");
        (await _store.GetAsync(operation.Id))!.State.Should().Be("Preparing");
    }

    [Fact]
    public async Task ConfirmedStageCannotRegenerateAndCleanupUsesItsOwnCap()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("cleanup-cap");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var confirmed = new SwapStageObservation("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            intent.Terms.GrossInput, intent.Terms.MinimumOutput, intent.Terms.PlatformFee.Amount, "3000", "0",
            "{\"receipt\":\"finalized-swap-fixture\"}");
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, confirmed)).Should().NotBeNull();
        var repeated = prepared.Plan with
        {
            Generation = 1, PreparationKey = SwapOperationIdentity.Preparation(operation.Id, "swap", 1, operation.IntentHash),
            ReservedNetworkFee = "0",
        };
        (await _store.ClaimNextStageAsync(operation.Id, 4, repeated, authority)).Should().BeNull();
        var consent = new SwapCleanupConsent(1, operation.Id, intent.QuoteHash, new string('a', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", intent.Terms.Budget.MaximumCleanupFee, true);
        var cleanup = prepared.Plan with
        {
            Stage = "CloseOptIn", Purpose = "Cleanup", ReservedNetworkFee = intent.Terms.Budget.MaximumCleanupFee, CleanupConsent = consent,
            PreparationKey = SwapOperationIdentity.CleanupPreparation(operation.Id, "CloseOptIn", 0, operation.IntentHash, consent),
        };
        (await _store.ClaimNextStageAsync(operation.Id, 4, cleanup, authority)).Should().BeNull();
        (await _store.BeginCleanupAsync(operation.Id, 4, consent, authority)).Should().NotBeNull();
        (await _store.ClaimNextStageAsync(operation.Id, 5, cleanup with { ReservedNetworkFee = "1001" }, authority)).Should().BeNull();
        var saved = (await _store.ClaimNextStageAsync(operation.Id, 5, cleanup, authority))!;
        saved.State.Should().Be("Planned");
        saved.PreviousStageId.Should().Be(stageId);
    }

    [Fact]
    public async Task CancellingAnUnsentCleanupReturnsToCleanupReadyAndReplansTheSameStageAtTheNextGeneration()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("cleanup-cancel");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var confirmed = new SwapStageObservation("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            intent.Terms.GrossInput, intent.Terms.MinimumOutput, intent.Terms.PlatformFee.Amount, "3000", "0",
            "{\"receipt\":\"finalized-swap-fixture\"}");
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, confirmed)).Should().NotBeNull();
        var consent = new SwapCleanupConsent(1, operation.Id, intent.QuoteHash, new string('b', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", intent.Terms.Budget.MaximumCleanupFee, true);
        SwapStagePlan Cleanup(int generation) => prepared.Plan with
        {
            Stage = "CloseOptIn", Generation = generation, Purpose = "Cleanup", ReservedNetworkFee = intent.Terms.Budget.MaximumCleanupFee,
            CleanupConsent = consent,
            PreparationKey = SwapOperationIdentity.CleanupPreparation(operation.Id, "CloseOptIn", generation, operation.IntentHash, consent),
        };
        (await _store.BeginCleanupAsync(operation.Id, 4, consent, authority)).Should().NotBeNull();
        var planned = (await _store.ClaimNextStageAsync(operation.Id, 5, Cleanup(0), authority))!;
        var plannedId = SurrealRecordGuid.BareId(planned.Id);

        (await _store.CancelUnsentStageAsync(operation.Id, 6, plannedId, planned.Version, Consent(authority))).Should().NotBeNull();

        var ready = (await _store.GetAsync(operation.Id))!;
        ready.State.Should().Be("CleanupReady", "an unsent cleanup cancellation must not strand the retained resource lane in Cancelled");
        ready.CleanupStarted.Should().BeTrue();
        (await _store.CancelUnsentStageAsync(operation.Id, 6, plannedId, planned.Version, Consent(authority)))!.State
            .Should().Be("Cancelled", "the response-loss replay returns the same cancelled stage");
        var effectRetry = prepared.Plan with
        {
            Generation = 1, PreparationKey = SwapOperationIdentity.Preparation(operation.Id, "swap", 1, operation.IntentHash),
        };
        (await _store.ClaimNextStageAsync(operation.Id, 7, effectRetry, authority)).Should().BeNull("CleanupReady admits only cleanup plans");
        var replanned = (await _store.ClaimNextStageAsync(operation.Id, 7, Cleanup(1), authority))!;
        replanned.Generation.Should().Be(1);
        replanned.PreviousStageId.Should().Be(plannedId);
        (await _store.GetAsync(operation.Id))!.State.Should().Be("Preparing");
    }

    [Fact]
    public async Task CancellingTheCleanupOfANeverSentOperationReturnsItToCancelledSoItsLaneCanBeHandedOver()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("cleanup-never-sent");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.CancelUnsentStageAsync(operation.Id, 2, stageId, 1, Consent(authority))).Should().NotBeNull();
        var consent = new SwapCleanupConsent(1, operation.Id, intent.QuoteHash, new string('c', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", intent.Terms.Budget.MaximumCleanupFee, true);
        var cleanup = prepared.Plan with
        {
            Stage = "CloseOptIn", Purpose = "Cleanup", ReservedNetworkFee = intent.Terms.Budget.MaximumCleanupFee, CleanupConsent = consent,
            PreparationKey = SwapOperationIdentity.CleanupPreparation(operation.Id, "CloseOptIn", 0, operation.IntentHash, consent),
        };
        (await _store.BeginCleanupAsync(operation.Id, 3, consent, authority)).Should().NotBeNull();
        var planned = (await _store.ClaimNextStageAsync(operation.Id, 4, cleanup, authority))!;
        var plannedId = SurrealRecordGuid.BareId(planned.Id);

        (await _store.CancelUnsentStageAsync(operation.Id, 5, plannedId, planned.Version, Consent(authority))).Should().NotBeNull();

        var cancelled = (await _store.GetAsync(operation.Id))!;
        cancelled.State.Should().Be("Cancelled", "no stage ever had egress, so nothing pins the allowance lane in CleanupReady");
        (await _store.CancelUnsentStageAsync(operation.Id, 5, plannedId, planned.Version, Consent(authority)))!.State
            .Should().Be("Cancelled", "the response-loss replay agrees with the no-egress outcome");
        (await _store.ClaimNextStageAsync(operation.Id, 6, cleanup with
        {
            Generation = 1, PreparationKey = SwapOperationIdentity.CleanupPreparation(operation.Id, "CloseOptIn", 1, operation.IntentHash, consent),
        }, authority)).Should().BeNull("a cancelled never-sent operation has nothing left to clean up");
    }
}
