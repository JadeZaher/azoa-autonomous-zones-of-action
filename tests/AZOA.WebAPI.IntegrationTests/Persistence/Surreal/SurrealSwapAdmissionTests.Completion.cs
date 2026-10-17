using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task CompletionCommitsImmutableReceiptAndOriginalReplayCannotChangeEffects()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("settlement-receipt");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        (await _store.CompleteAsync(operation.Id, 3, stageId, 2, [])).Should().BeNull();
        var observation = new SwapStageObservation("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            intent.Terms.GrossInput, intent.Terms.MinimumOutput, intent.Terms.PlatformFee.Amount, "1000", "0", "{\"receipt\":\"fixture\"}");
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, observation)).Should().NotBeNull();
        var completed = (await _store.CompleteAsync(operation.Id, 4, stageId, 3, []))!;
        completed.State.Should().Be("Completed");
        completed.Version.Should().Be(5);
        var receipt = JsonSerializer.Deserialize<SwapSettlementReceipt>(completed.SettlementReceiptJson!)!;
        receipt.ActualOutput.Should().Be(intent.Terms.MinimumOutput);
        receipt.TransactionIds.Should().Equal(prepared.TransactionIds);
        var replay = (await _store.CompleteAsync(operation.Id, 4, stageId, 3, []))!;
        replay.SettlementReceiptJson.Should().Be(completed.SettlementReceiptJson);
        replay.Version.Should().Be(5);
        (await _store.CompleteAsync(operation.Id, 5, stageId, 4,
            [new("Invented", "asset", "0", "owner", "other", true)])).Should().BeNull();
        (await _store.GetAsync(operation.Id))!.ConfirmedEffectNetworkFee.Should().Be("1000");
    }

    [Theory]
    [InlineData("prerequisite")]
    [InlineData("below-minimum")]
    public async Task ConfirmedStageWithoutExactSwapEconomicsCannotCompleteOperation(string mutation)
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("non-settlement-" + mutation);
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var observation = new SwapStageObservation("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            mutation == "prerequisite" ? "0" : intent.Terms.GrossInput,
            mutation == "below-minimum" ? "0" : intent.Terms.MinimumOutput,
            intent.Terms.PlatformFee.Amount, "1000", "0", "{\"receipt\":\"fixture\"}");
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, observation)).Should().NotBeNull();
        (await _store.CompleteAsync(operation.Id, 4, stageId, 3, [])).Should().BeNull();
        var saved = (await _store.GetAsync(operation.Id))!;
        saved.State.Should().Be("StageResolved");
        saved.SettlementReceiptJson.Should().BeNull();
    }
}
