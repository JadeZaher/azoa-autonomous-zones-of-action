using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task EarlierCompetingOptInKeepsUnknownActualAndReservationUntilExactlyOnceOrderedProof()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("uncertain-rent", optIn: true);
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var effect = new SwapStageObservation("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            "0", "0", "0", "1000", null, "{\"effect\":\"opt-in-confirmed-before-order-proof\"}");
        var confirmed = (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, effect))!;
        var current = (await _store.GetAsync(operation.Id))!;
        current.State.Should().Be("StageResolved");
        current.ConfirmedRetainedRent.Should().Be("0");
        current.UnresolvedRentReservation.Should().Be("100000");
        current.UnresolvedRentStageCount.Should().Be(1);
        JsonSerializer.Deserialize<SwapStageObservation>(confirmed.ObservationJson!)!.LockedBalance.Should().BeNull();
        var next = prepared.Plan with
        {
            Stage = "another-opt-in", ReservedNetworkFee = "2000", ReservedLockedBalance = "1",
            PreparationKey = SwapOperationIdentity.Preparation(operation.Id, "another-opt-in", 0, operation.IntentHash),
        };
        (await _store.ClaimNextStageAsync(operation.Id, current.Version, next, authority)).Should().BeNull();
        const string proof = "{\"orderedHistory\":\"earlier-competing-opt-in-fixture\"}";
        await Task.WhenAll(_store.ResolveRentAccountingAsync(operation.Id, current.Version, stageId, confirmed.Version, "0", proof),
            _store.ResolveRentAccountingAsync(operation.Id, current.Version, stageId, confirmed.Version, "0", proof));
        current = (await _store.GetAsync(operation.Id))!;
        current.UnresolvedRentStageCount.Should().Be(0);
        current.UnresolvedRentReservation.Should().Be("0");
        current.ConfirmedRetainedRent.Should().Be("0");
        var resolved = (await _store.GetStageAsync(stageId))!;
        resolved.ObservationJson.Should().Be(confirmed.ObservationJson);
        resolved.ResolvedLockedBalance.Should().Be("0");
        (await _store.ResolveRentAccountingAsync(operation.Id, 4, stageId, confirmed.Version, "0", proof)).Should().NotBeNull();
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(current.Version);
        (await _store.ResolveRentAccountingAsync(operation.Id, current.Version, stageId, resolved.Version, "100000", proof)).Should().BeNull();
        (await _store.ClaimNextStageAsync(operation.Id, current.Version, next, authority)).Should().NotBeNull();
    }
}
