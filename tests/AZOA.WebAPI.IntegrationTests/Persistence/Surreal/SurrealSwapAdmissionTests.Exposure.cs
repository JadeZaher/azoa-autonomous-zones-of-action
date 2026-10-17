using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task ExposedAbsenceReleasesOnlyAfterTerminalProofWithoutInventingSignedWire()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, "external-absence");
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var plan = StagePlan(operation, terms);
        var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, plan, authority))!;
        var id = SurrealRecordGuid.BareId(stage.Id);
        stage = (await _store.ExposeStageAsync(operation.Id, 1, id, stage.Version, new string('a', 64), ["tx"], authority))!;
        var absent = new SwapStageObservation("ExpiredAbsent", ["tx"], terms.Network.ProtocolIdentity,
            "0", "0", "0", "0", "0", "{\"fixtureFinalizedAbsence\":true}");
        (await _store.RecordObservationAsync(operation.Id, 2, id, stage.Version,
            absent with { Verdict = "Confirmed" })).Should().BeNull();
        (await _store.RecordObservationAsync(operation.Id, 2, id, stage.Version,
            absent with { TransactionIds = ["another"] })).Should().BeNull();
        stage = (await _store.RecordObservationAsync(operation.Id, 2, id, stage.Version, absent))!;
        stage.PreparedJson.Should().BeNull();
        stage.AuthorizedAt.Should().BeNull();
        var parent = (await _store.GetAsync(operation.Id))!;
        parent.State.Should().Be("StageResolved");
        parent.ConfirmedEffectNetworkFee.Should().Be("0");
        parent.ConfirmedRetainedRent.Should().Be("0");
        (await _store.RecordObservationAsync(operation.Id, 2, id, stage.Version - 1, absent))!.Version.Should().Be(stage.Version);
    }

    [Fact]
    public async Task UploadedExposedSignatureStillRequiresSeparateLiveServerAuthorization()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, "external-upload");
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var plan = StagePlan(operation, terms);
        var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, plan, authority))!;
        var id = SurrealRecordGuid.BareId(stage.Id);
        stage = (await _store.ExposeStageAsync(operation.Id, 1, id, stage.Version, new string('b', 64), ["tx"], authority))!;
        var candidate = new PreparedSwapStage(plan, ["tx"], "{\"realSignatureFixture\":true}");
        (await _store.SaveExposedCandidateAsync(operation.Id, 2, id, stage.Version,
            candidate with { TransactionIds = ["different"] })).Should().BeNull();
        stage = (await _store.SaveExposedCandidateAsync(operation.Id, 2, id, stage.Version, candidate))!;
        stage.State.Should().Be("ExternalExposed");
        stage.AuthorizedAt.Should().BeNull();
        (await _store.CancelUnsentStageAsync(operation.Id, 3, id, stage.Version, Consent(authority))).Should().BeNull();
        (await _store.AuthorizeStageAsync(operation.Id, 3, id, stage.Version,
            authority with { Credential = authority.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } })).Should().BeNull();
        var authorized = (await _store.AuthorizeStageAsync(operation.Id, 3, id, stage.Version, authority))!;
        authorized.AuthorizedAt.Should().NotBeNull();
        authorized.ExternalExposedAt.Should().Be(stage.ExternalExposedAt);
        var withdrawn = (await _store.WithdrawSubmissionAsync(operation.Id, 4, id, authorized.Version, Consent(authority)))!;
        withdrawn.ServerSubmissionWithdrawnAt.Should().NotBeNull();
        withdrawn.State.Should().Be("SubmissionAuthorized");
        withdrawn.AuthorizedAt.Should().Be(authorized.AuthorizedAt);
        (await _store.GetAsync(operation.Id))!.State.Should().Be("Pending");
        (await _store.AuthorizeStageAsync(operation.Id, 5, id, withdrawn.Version, authority)).Should().BeNull();
        (await _store.CancelUnsentStageAsync(operation.Id, 5, id, withdrawn.Version, Consent(authority))).Should().BeNull();
        (await _store.WithdrawSubmissionAsync(operation.Id, 4, id, authorized.Version, Consent(authority)))!.Version.Should().Be(withdrawn.Version);
    }

    [Fact]
    public async Task ExternalExposurePrecedesDisclosureAndCannotBeCancelledAsUnsent()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, "external-exposure");
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, StagePlan(operation, terms), authority))!;
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var hash = new string('e', 64);
        var exposed = (await _store.ExposeStageAsync(operation.Id, 1, stageId, stage.Version, hash, ["unsigned-derived-id"], authority))!;
        exposed.State.Should().Be("ExternalExposed");
        exposed.ExternalExposedAt.Should().NotBeNull();
        exposed.PreparedJson.Should().BeNull();
        exposed.AuthorizedAt.Should().BeNull();
        (await _store.GetAsync(operation.Id))!.State.Should().Be("Pending");
        (await _store.CancelUnsentStageAsync(operation.Id, 2, stageId, exposed.Version, Consent(authority))).Should().BeNull();
        (await _store.AdmitCustodyAsync(operation.Id, 2, stageId, exposed.Version, authority)).Should().BeNull();
        var replay = (await _store.ExposeStageAsync(operation.Id, 1, stageId, stage.Version, hash, ["unsigned-derived-id"], authority))!;
        replay.Version.Should().Be(exposed.Version);
        (await _store.ExposeStageAsync(operation.Id, 1, stageId, stage.Version, hash, ["different-id"], authority)).Should().BeNull();
        (await _store.ExposeStageAsync(operation.Id, 1, stageId, stage.Version, hash, ["unsigned-derived-id"],
            authority with { Credential = authority.Credential with { ActorUserId = Guid.NewGuid() } })).Should().BeNull();
    }

    [Fact]
    public async Task DisclosureRacingUnsentCancellationHasOneDurableWinner()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, "exposure-cancel-race");
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, StagePlan(operation, terms), authority))!;
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        var results = await Task.WhenAll(
            _store.ExposeStageAsync(operation.Id, 1, stageId, stage.Version, new string('d', 64), [], authority),
            _store.CancelUnsentStageAsync(operation.Id, 1, stageId, stage.Version, Consent(authority)));
        results.Where(result => result is not null).Should().ContainSingle();
        var saved = (await _store.GetStageAsync(stageId))!;
        var parent = (await _store.GetAsync(operation.Id))!;
        if (saved.ExternalExposedAt is not null)
        {
            saved.State.Should().Be("ExternalExposed");
            parent.State.Should().Be("Pending");
        }
        else
        {
            saved.State.Should().Be("Cancelled");
            parent.State.Should().Be("Cancelled");
        }
        saved.PreparedJson.Should().BeNull();
        saved.AuthorizedAt.Should().BeNull();
    }
}
