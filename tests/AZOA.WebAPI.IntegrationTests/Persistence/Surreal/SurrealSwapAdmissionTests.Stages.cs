using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task CompetingPlansRetainOneOriginalPlanAndNeverAuthorizeEgress()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, "plans");
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var first = StagePlan(operation, terms);
        var second = first with { UnsignedPayloadJson = "{\"candidate\":2}" };
        var results = await Task.WhenAll(_store.ClaimInitialStageAsync(operation.Id, 0, first, authority),
            _store.ClaimInitialStageAsync(operation.Id, 0, second, authority));
        results.Where(result => result is not null).Should().ContainSingle();
        var stage = results.Single(result => result is not null)!;
        stage.State.Should().Be("Planned");
        stage.PreparedJson.Should().BeNull();
        stage.AuthorizedAt.Should().BeNull();
        var reconstructed = new SurrealSwapOperationStore(_executor);
        (await reconstructed.GetStageAsync(SurrealRecordGuid.BareId(stage.Id)))!.PlanJson.Should().Be(stage.PlanJson);
        (await reconstructed.GetAsync(operation.Id))!.Version.Should().Be(1);
    }

    [Fact]
    public async Task PreparedSignatureDoesNotAuthorizeAndCancellationBlocksLateFirstSend()
    {
        var (operation, stage, authority, _) = await PreparedStageAsync("cancel-prepared");
        stage.State.Should().Be("Prepared");
        stage.AuthorizedAt.Should().BeNull();
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var cancelled = await _store.CancelUnsentStageAsync(operation.Id, 2, stageId, 1, Consent(authority));
        cancelled!.State.Should().Be("Cancelled");
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().BeNull();
        (await _store.GetAsync(operation.Id))!.State.Should().Be("Cancelled");
        (await _store.GetStageAsync(stageId))!.AuthorizedAt.Should().BeNull();
    }

    [Fact]
    public async Task FirstSendRacingCancellationHasOnlyOneConsistentOutcome()
    {
        var (operation, stage, authority, _) = await PreparedStageAsync("race-first-send");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var results = await Task.WhenAll(_store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority),
            _store.CancelUnsentStageAsync(operation.Id, 2, stageId, 1, Consent(authority)));
        results.Where(result => result is not null).Should().ContainSingle();
        var current = (await _store.GetAsync(operation.Id))!;
        var savedStage = (await _store.GetStageAsync(stageId))!;
        if (current.State == "Pending")
        {
            savedStage.State.Should().Be("SubmissionAuthorized");
            savedStage.AuthorizedAt.Should().NotBeNull();
        }
        else
        {
            current.State.Should().Be("Cancelled");
            savedStage.State.Should().Be("Cancelled");
            savedStage.AuthorizedAt.Should().BeNull();
        }
        current.Version.Should().Be(3);
        savedStage.Version.Should().Be(2);
    }

    [Fact]
    public async Task CurrentUserRevocationStopsFirstSendButNotReplayOfAlreadyAuthorizedBytes()
    {
        var (operation, stage, authority, _) = await PreparedStageAsync("revocation");
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(operation.ActorUserId)
            .Set(row => row.IsActive, false).Build())).EnsureAllOk();
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority)).Should().BeNull();
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(2);
        (await _store.GetStageAsync(stageId))!.State.Should().Be("Prepared");
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(operation.ActorUserId)
            .Set(row => row.IsActive, true).Build())).EnsureAllOk();
        var authorized = (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1, authority))!;
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(operation.ActorUserId)
            .Set(row => row.IsActive, false).Build())).EnsureAllOk();
        var replay = await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1,
            authority with { Credential = authority.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } });
        replay!.PreparedHash.Should().Be(authorized.PreparedHash);
        replay.AuthorizedAt.Should().Be(authorized.AuthorizedAt);
        (await _store.AuthorizeStageAsync(operation.Id, 2, stageId, 1,
            authority with { Credential = authority.Credential with { ActorUserId = Guid.NewGuid() } })).Should().BeNull();
    }

    [Fact]
    public async Task ChangedSignedCandidateAndOverBudgetPlanAreRefused()
    {
        var (operation, stage, authority, prepared) = await PreparedStageAsync("immutable-signature");
        (await _store.SaveSignedStageAsync(operation.Id, 2, SurrealRecordGuid.BareId(stage.Id), 1,
            prepared with { SignedPayloadJson = "{\"wire\":\"changed\"}" })).Should().BeNull();
        var (quote, terms, otherAuthority) = await SeedAsync();
        var other = Operation(quote, terms, "over-budget");
        (await _store.AdmitAsync(other, otherAuthority)).Should().NotBeNull();
        var plan = StagePlan(other, terms) with { ReservedNetworkFee = "3001" };
        (await _store.ClaimInitialStageAsync(other.Id, 0, plan, otherAuthority)).Should().BeNull();
        (await _store.GetAsync(other.Id))!.CurrentStageId.Should().BeNull();
        (await _store.GetStageAsync(SwapOperationIdentity.Stage(plan.PreparationKey))).Should().BeNull();
        (await _store.CancelUnsentStageAsync(operation.Id, 2, SurrealRecordGuid.BareId(stage.Id), 1, Consent(authority)))!
            .State.Should().Be("Cancelled");
    }

    private async Task<(SwapOperationRecord, SwapStageRecord, SigningAdmissionAuthority, PreparedSwapStage)> PreparedStageAsync(string key, bool optIn = false)
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, key);
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var plan = StagePlan(operation, terms);
        if (optIn) plan = plan with
        {
            Stage = "OptIn", PreparationKey = SwapOperationIdentity.Preparation(operation.Id, "OptIn", 0, operation.IntentHash),
            ReservedLockedBalance = "100000",
        };
        var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, plan, authority))!;
        var prepared = new PreparedSwapStage(plan, ["journal-fixture-transaction"], "{\"wire\":\"journal-fixture\"}");
        var saved = (await _store.SaveSignedStageAsync(operation.Id, 1, SurrealRecordGuid.BareId(stage.Id), 0, prepared))!;
        return (operation, saved, authority, prepared);
    }

    private static SwapStagePlan StagePlan(SwapOperationRecord operation, DexQuoteTerms terms)
        => new("swap", 0, SwapOperationIdentity.Preparation(operation.Id, "swap", 0, operation.IntentHash),
            operation.IntentHash, [terms.Owner.Address], "{\"candidate\":1}", "{\"protocol\":\"fixture\"}", "3000", "0");
}
