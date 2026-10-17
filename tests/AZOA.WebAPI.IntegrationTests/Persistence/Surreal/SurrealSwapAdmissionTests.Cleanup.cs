using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task CleanupNeedsNewParticipantConsentAndDoesNotRequireOriginalWorkflowToBeRunning()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("cleanup-new-command");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var observation = new SwapStageObservation("Confirmed", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            intent.Terms.GrossInput, intent.Terms.MinimumOutput, intent.Terms.PlatformFee.Amount, "1000", "0", "{\"receipt\":\"fixture\"}");
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, observation)).Should().NotBeNull();
        var completed = (await _store.CompleteAsync(operation.Id, 4, stageId, 3, []))!;
        // The original workflow is historical; this new command must not seek its execution fence.
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<SwapOperationRecord>(operation.Id)
            .Set(row => row.ActionRunId, Guid.NewGuid().ToString("N"))
            .Set(row => row.ActionNodeId, Guid.NewGuid().ToString("N"))
            .Set(row => row.ActionIntentHash, new string('a', 64)).Build())).EnsureAllOk();
        var consent = new SwapCleanupConsent(1, operation.Id, intent.QuoteHash, new string('b', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", "1000", true);
        (await _store.BeginCleanupAsync(operation.Id, 5, consent, authority with { Kind = "Workflow" })).Should().BeNull();
        (await _store.BeginCleanupAsync(operation.Id, 5, consent with { AcceptCurrentZeroHoldingReplacement = false }, authority)).Should().BeNull();
        var admitted = (await _store.BeginCleanupAsync(operation.Id, 5, consent, authority))!;
        admitted.State.Should().Be("CleanupReady");
        admitted.SettlementReceiptJson.Should().Be(completed.SettlementReceiptJson);
        admitted.Version.Should().Be(6);
        (await _store.BeginCleanupAsync(operation.Id, 5, consent, authority))!.Version.Should().Be(6);
        (await _store.BeginCleanupAsync(operation.Id, 6, consent with { RequestKeyHash = new string('c', 64) }, authority)).Should().BeNull();
    }

    [Fact]
    public async Task PendingOriginalStageCannotBecomeCleanupReady()
    {
        var (operation, stage, authority, _) = await PreparedStageAsync("cleanup-pending");
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, SurrealRecordGuid.BareId(stage.Id), 1, authority)).Should().NotBeNull();
        var consent = new SwapCleanupConsent(1, operation.Id, intent.QuoteHash, new string('a', 64), intent.Terms.ActorUserId,
            null, intent.Terms.Owner, intent.Terms.OutputAsset.Id, "CloseCurrentZeroHolding", "1000", true);
        (await _store.BeginCleanupAsync(operation.Id, 3, consent, authority)).Should().BeNull();
        (await _store.GetAsync(operation.Id))!.CleanupConsentJson.Should().BeNull();
        (await _store.GetAsync(operation.Id))!.State.Should().Be("Pending");
    }
}
