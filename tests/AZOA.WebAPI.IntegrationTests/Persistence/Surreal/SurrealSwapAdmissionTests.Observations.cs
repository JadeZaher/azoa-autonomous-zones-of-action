using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task FinalizedAlternateCandidateIsAppendOnlyAndSettlementNamesActualWinnerExactlyOnce()
    {
        var (operation, stage, authority, original) = await PreparedStageAsync("alternate-finalized");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var winner = original with { TransactionIds = ["actual-finalized-signature-hash"], SignedPayloadJson = "\"actual-winner-wire-fixture\"" };
        var observation = new SwapStageObservation("Confirmed", winner.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            intent.Terms.GrossInput, intent.Terms.MinimumOutput, intent.Terms.PlatformFee.Amount, "1000", "0", "{\"finalized\":true}",
            FinalizedCandidate: new(winner, CanonicalJson.Hash(original), CanonicalJson.Hash(original.Plan)));
        var results = await Task.WhenAll(
            _store.RecordObservationAsync(operation.Id, 3, stageId, 2, observation),
            _store.RecordObservationAsync(operation.Id, 3, stageId, 2, observation));
        results.Should().Contain(row => row != null);
        var saved = (await _store.GetStageAsync(stageId))!;
        saved.PreparedJson.Should().Be(CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(original)));
        saved.PreparedHash.Should().Be(CanonicalJson.Hash(original));
        (await _store.GetAsync(operation.Id))!.ConfirmedEffectNetworkFee.Should().Be("1000");
        var completed = (await _store.CompleteAsync(operation.Id, 4, stageId, 3, []))!;
        var receipt = JsonSerializer.Deserialize<SwapSettlementReceipt>(completed.SettlementReceiptJson!)!;
        receipt.TransactionIds.Should().Equal(winner.TransactionIds);
        receipt.OriginalTransactionIds.Should().Equal(original.TransactionIds);
        receipt.OriginalPreparedHash.Should().Be(saved.PreparedHash);
        receipt.PlanHash.Should().Be(saved.PlanHash);
        (await _store.RecordObservationAsync(operation.Id, 5, stageId, 4,
            observation with { ActualOutput = "999999" })).Should().BeNull();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("plan")]
    [InlineData("original")]
    [InlineData("identity")]
    public async Task AlternateCandidateCannotChangePlanOriginalIdentityOrPendingState(string mutation)
    {
        var (operation, stage, authority, original) = await PreparedStageAsync("alternate-invalid-" + mutation);
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var winner = original with { TransactionIds = ["winner"], SignedPayloadJson = "\"winner\"" };
        var binding = new SwapFinalizedCandidate(winner, CanonicalJson.Hash(original), CanonicalJson.Hash(original.Plan));
        if (mutation == "plan") binding = binding with { Candidate = winner with { Plan = winner.Plan with { UnsignedPayloadJson = "\"changed\"" } } };
        if (mutation == "original") binding = binding with { OriginalPreparedHash = new string('0', 64) };
        var observation = new SwapStageObservation("Confirmed", mutation == "identity" ? ["not-winner"] : winner.TransactionIds,
            intent.Terms.Network.ProtocolIdentity, intent.Terms.GrossInput, intent.Terms.MinimumOutput, intent.Terms.PlatformFee.Amount,
            "1000", "0", "{\"finalized\":true}", FinalizedCandidate: binding);
        if (mutation == "pending") observation = observation with
        { Verdict = "Pending", ActualInput = null, ActualOutput = null, PlatformFeePaid = null, NetworkFeePaid = null, LockedBalance = null };
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, observation)).Should().BeNull();
        (await _store.GetStageAsync(stageId))!.Version.Should().Be(2);
    }

    [Fact]
    public async Task HashAndPendingEvidenceCannotCompleteSwapAndTerminalEvidenceCannotChange()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("observe");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var pending = new SwapStageObservation("Pending", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity);
        (await _store.RecordObservationAsync(operation.Id, 2, stageId, 1, pending)).Should().BeNull();
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, pending))!.State.Should().Be("SubmissionAuthorized");
        (await _store.GetAsync(operation.Id))!.State.Should().Be("Pending");
        var confirmed = pending with
        {
            Verdict = "Confirmed", ActualInput = intent.Terms.GrossInput, ActualOutput = intent.Terms.MinimumOutput,
            PlatformFeePaid = intent.Terms.PlatformFee.Amount, NetworkFeePaid = "1000", LockedBalance = "0",
            EvidenceJson = "{\"finalized\":\"provider-fixture-proof\"}",
        };
        var saved = (await _store.RecordObservationAsync(operation.Id, 4, stageId, 3, confirmed))!;
        saved.State.Should().Be("Confirmed");
        (await _store.GetAsync(operation.Id))!.State.Should().Be("StageResolved");
        (await _store.RecordObservationAsync(operation.Id, 4, stageId, 3, confirmed))!.ObservationJson.Should().Be(saved.ObservationJson);
        (await _store.GetAsync(operation.Id))!.ConfirmedEffectNetworkFee.Should().Be("1000");
        (await _store.RecordObservationAsync(operation.Id, 5, stageId, 4,
            confirmed with { ActualOutput = "999999999" })).Should().BeNull();
    }

    [Theory]
    [InlineData("network")]
    [InlineData("transaction")]
    [InlineData("over-budget")]
    [InlineData("expired-with-cost")]
    [InlineData("failed-with-output")]
    [InlineData("pending-with-output")]
    [InlineData("missing-evidence")]
    public async Task InconsistentObservationDoesNotAdvanceEitherEpoch(string mutation)
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("invalid-" + mutation);
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var observation = new SwapStageObservation("FailedOnChain", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            "0", "0", "0", "1000", "0", "{\"receipt\":\"finalized-revert-fixture\"}");
        observation = mutation switch
        {
            "network" => observation with { ProtocolIdentity = "wrong-network" },
            "transaction" => observation with { TransactionIds = ["different-transaction"] },
            "over-budget" => observation with { NetworkFeePaid = "3001" },
            "expired-with-cost" => observation with { Verdict = "ExpiredAbsent" },
            "failed-with-output" => observation with { ActualOutput = "1" },
            "pending-with-output" => observation with { Verdict = "Pending" },
            _ => observation with { EvidenceJson = null },
        };
        (await _store.RecordObservationAsync(operation.Id, 3, stageId, 2, observation)).Should().BeNull();
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(3);
        (await _store.GetStageAsync(stageId))!.Version.Should().Be(2);
    }

    [Fact]
    public async Task CompetingTerminalProofsCannotOverwriteWinner()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("competing-proof");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().NotBeNull();
        var failed = new SwapStageObservation("FailedOnChain", prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity,
            "0", "0", "0", "1000", "0", "{\"receipt\":\"finalized-revert-fixture\"}");
        var absent = failed with { Verdict = "ExpiredAbsent", NetworkFeePaid = "0", EvidenceJson = "{\"absence\":\"finalized-window-fixture\"}" };
        var results = await Task.WhenAll(_store.RecordObservationAsync(operation.Id, 3, stageId, 2, failed),
            _store.RecordObservationAsync(operation.Id, 3, stageId, 2, absent));
        results.Count(result => result is not null).Should().Be(1);
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(4);
        (await _store.GetStageAsync(stageId))!.ObservationJson.Should().Be(results.Single(result => result is not null)!.ObservationJson);
    }
}
