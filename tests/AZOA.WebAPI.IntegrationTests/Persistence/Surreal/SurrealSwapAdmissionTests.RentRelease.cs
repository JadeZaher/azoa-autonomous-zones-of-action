using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task ReleaseCheckpointsSurviveLongHistoryAndConcurrentFinalProofReleasesOnlyOnce()
    {
        var (operation, allocation, authority, prepared) = await PreparedStageAsync("rent-release-history", true);
        var allocationId = SurrealRecordGuid.BareId(allocation.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        allocation = (await _store.AuthorizeStageAsync(operation.Id, 2, allocationId, 1, authority))!;
        allocation = (await _store.RecordObservationAsync(operation.Id, 3, allocationId, allocation.Version,
            new("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
                "0", "0", "0", "1000", "100000", "{\"allocation\":\"ordered-original-fixture\"}")))!;
        var consent = new SwapCleanupConsent(1, operation.Id, intent.QuoteHash, new string('c', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", "1000", true);
        operation = (await _store.BeginCleanupAsync(operation.Id, 4, consent, authority))!;
        var plan = prepared.Plan with
        {
            Stage = "CloseOptIn", Purpose = "Cleanup", CleanupConsent = consent,
            PreparationKey = SwapOperationIdentity.CleanupPreparation(operation.Id, "CloseOptIn", 0, operation.IntentHash, consent),
            ReservedNetworkFee = "1000", ReservedLockedBalance = "0",
        };
        var cleanup = (await _store.ClaimNextStageAsync(operation.Id, operation.Version, plan, authority))!;
        var cleanupId = SurrealRecordGuid.BareId(cleanup.Id);
        operation = (await _store.GetAsync(operation.Id))!;
        var closed = new PreparedSwapStage(plan, ["cleanup-history-fixture"], "{\"wire\":\"cleanup-fixture\"}");
        cleanup = (await _store.SaveSignedStageAsync(operation.Id, operation.Version, cleanupId, cleanup.Version, closed))!;
        operation = (await _store.GetAsync(operation.Id))!;
        cleanup = (await _store.AuthorizeStageAsync(operation.Id, operation.Version, cleanupId, cleanup.Version, authority))!;
        operation = (await _store.GetAsync(operation.Id))!;
        cleanup = (await _store.RecordObservationAsync(operation.Id, operation.Version, cleanupId, cleanup.Version,
            new("Confirmed", closed.TransactionIds, intent.Terms.Network.ProtocolIdentity,
                "0", "0", "0", "1000", "0", "{\"cleanup\":\"exact-finalized-fixture\"}")))!;
        for (var page = 0; page < 66; page++)
        {
            operation = (await _store.GetAsync(operation.Id))!;
            allocation = (await _store.GetStageAsync(allocationId))!;
            cleanup = (await _store.GetStageAsync(cleanupId))!;
            var progress = new SwapRentReleaseProgress(JsonSerializer.Serialize(new { page, cursor = "next-" + page }), false);
            allocation = (await _store.RecordRentReleaseAsync(operation.Id, operation.Version, allocationId, allocation.Version,
                cleanupId, cleanup.Version, progress))!;
            allocation.RentReleaseScanJson.Should().Be(CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { page, cursor = "next-" + page })));
            (await _store.GetAsync(operation.Id))!.ConfirmedReleasedRent.Should().Be("0");
        }
        operation = (await _store.GetAsync(operation.Id))!;
        allocation = (await _store.GetStageAsync(allocationId))!;
        cleanup = (await _store.GetStageAsync(cleanupId))!;
        var final = new SwapRentReleaseProgress("{\"complete\":true,\"pages\":67}", true, "100000",
            "{\"releasedBy\":\"EarlierExternalClose\",\"allocation\":\"original\",\"history\":\"exhausted\"}");
        (await _store.CompleteCleanupAsync(operation.Id, operation.Version, cleanupId, cleanup.Version)).Should().BeNull();
        (await _store.RecordRentReleaseAsync(operation.Id, operation.Version, allocationId, allocation.Version,
            cleanupId, cleanup.Version, final with { ReleasedLockedBalance = "100001" })).Should().BeNull();
        await Task.WhenAll(_store.RecordRentReleaseAsync(operation.Id, operation.Version, allocationId, allocation.Version, cleanupId, cleanup.Version, final),
            _store.RecordRentReleaseAsync(operation.Id, operation.Version, allocationId, allocation.Version, cleanupId, cleanup.Version, final));
        var committed = (await _store.GetAsync(operation.Id))!;
        committed.ConfirmedReleasedRent.Should().Be("100000");
        committed.ConfirmedRetainedRent.Should().Be("100000");
        committed.Version.Should().Be(operation.Version + 1);
        (await _store.RecordRentReleaseAsync(operation.Id, operation.Version, allocationId, allocation.Version,
            cleanupId, cleanup.Version, final)).Should().NotBeNull();
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(committed.Version);
        (await _store.RecordRentReleaseAsync(operation.Id, operation.Version, allocationId, allocation.Version,
            cleanupId, cleanup.Version, final with { EvidenceJson = "{\"releasedBy\":\"Cleanup\"}" })).Should().BeNull();
        cleanup = (await _store.GetStageAsync(cleanupId))!;
        var completed = (await _store.CompleteCleanupAsync(operation.Id, committed.Version, cleanupId, cleanup.Version))!;
        completed.State.Should().Be("CleanupCompleted");
        var receipt = JsonSerializer.Deserialize<SwapCleanupReceipt>(completed.CleanupReceiptJson!)!;
        receipt.ReleasedRent.Should().Be("100000");
        receipt.TransactionIds.Should().Equal(closed.TransactionIds);
        receipt.OriginalEconomicSettlementPreserved.Should().BeFalse();
        (await _store.CompleteCleanupAsync(operation.Id, committed.Version, cleanupId, cleanup.Version))!
            .CleanupReceiptJson.Should().Be(completed.CleanupReceiptJson);
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(completed.Version);
    }
}
