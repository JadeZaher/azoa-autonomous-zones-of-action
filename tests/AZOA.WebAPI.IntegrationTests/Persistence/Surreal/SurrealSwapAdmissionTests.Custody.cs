using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task DelayedPlanReplayCannotAdmitCustodyAfterCancellation()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, "delayed-custody");
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var plan = StagePlan(operation, terms);
        var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, plan, authority))!;
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        (await _store.CancelUnsentStageAsync(operation.Id, 1, stageId, 0, Consent(authority))).Should().NotBeNull();
        (await _store.ClaimInitialStageAsync(operation.Id, 0, plan, authority)).Should().NotBeNull();
        (await _store.AdmitCustodyAsync(operation.Id, 1, stageId, 0, authority)).Should().BeNull();
        (await _store.GetStageAsync(stageId))!.PreparedJson.Should().BeNull();
    }

    [Fact]
    public async Task LiveRevocationAndConcurrentCustodyAdmissionPreserveEpochs()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = Operation(quote, terms, "custody-authority");
        (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
        var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, StagePlan(operation, terms), authority))!;
        var stageId = SurrealRecordGuid.BareId(stage.Id);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(operation.ActorUserId)
            .Set(row => row.IsActive, false).Build())).EnsureAllOk();
        (await _store.AdmitCustodyAsync(operation.Id, 1, stageId, 0, authority)).Should().BeNull();
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(1);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(operation.ActorUserId)
            .Set(row => row.IsActive, true).Build())).EnsureAllOk();
        var claims = await Task.WhenAll(_store.AdmitCustodyAsync(operation.Id, 1, stageId, 0, authority),
            _store.AdmitCustodyAsync(operation.Id, 1, stageId, 0, authority));
        claims.Count(item => item is not null).Should().Be(1);
        var admitted = claims.Single(item => item is not null)!;
        admitted.State.Should().Be("Planned");
        admitted.AuthorizedAt.Should().BeNull();
        admitted.Version.Should().Be(1);
        (await _store.GetAsync(operation.Id))!.Version.Should().Be(2);
        (await _store.CancelUnsentStageAsync(operation.Id, 2, stageId, 1, Consent(authority))).Should().NotBeNull();
        (await _store.GetStageAsync(stageId))!.AuthorizedAt.Should().BeNull();
    }
}
