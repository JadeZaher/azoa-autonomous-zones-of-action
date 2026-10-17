using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Theory]
    [InlineData("tenant-key")]
    [InlineData("participant-grant")]
    [InlineData("participant-wallet")]
    [InlineData("participant-login")]
    public async Task CommittedSourceCannotStartPreparationAfterAnyOriginalAuthorityChanges(string change)
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        source = (await _store.CommitExchangeAsync(source.Id))!;
        await ChangeAuthorityAsync(change, intent.ApiKeyId, authority.Grant.Id, authority.Wallet.Id, authority.ParticipantUserId);

        (await _store.BeginExchangePreparationAsync(source.Id, source.Version)).Should().BeNull();

        var persisted = (await _store.GetExchangeSourceAsync(source.Id))!;
        persisted.State.Should().Be("Committed");
        persisted.WireIntentJson.Should().BeNull("no provider intent may be claimed for custody without both live authorities");
        (await Counters()).Held.Should().Be(400, "the commitment stays until settlement, absence proof or abandonment");
    }

    [Fact]
    public async Task FinalizedEvidenceAndSettlementAccountAfterBothOriginalAuthoritiesAreGone()
    {
        var (intent, authority) = await ExchangeAsync();
        var source = await SubmittedAsync(intent, authority);
        await ChangeAuthorityAsync("tenant-key", intent.ApiKeyId, authority.Grant.Id, authority.Wallet.Id, authority.ParticipantUserId);
        await ChangeAuthorityAsync("participant-grant", intent.ApiKeyId, authority.Grant.Id, authority.Wallet.Id, authority.ParticipantUserId);
        await SeedTreasuryAsync();

        var recorded = await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, Verdict(source, "Confirmed"));
        var settled = await _store.CompleteExchangeSettlementAsync(source.Id);

        recorded!.State.Should().Be("Confirmed");
        settled.Should().NotBeNull("a landed purchase is accounted for without renewed signing authority");
        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Completed");
        (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("Held");
        (await TreasuryCounters()).Credited.Should().Be(10);
    }

    [Fact]
    public async Task ConcurrentAdmissionOfOneRequestJournalsExactlyOneSource()
    {
        var (intent, authority) = await ExchangeAsync();

        var admitted = await Task.WhenAll(_store.AdmitExchangeSourceAsync(intent, authority),
            _store.AdmitExchangeSourceAsync(intent, authority)).WaitAsync(TimeSpan.FromSeconds(15));

        admitted.Should().OnlyContain(row => row != null);
        admitted[0]!.Id.Should().Be(admitted[1]!.Id);
        (await _executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From())).Should().ContainSingle();
        (await Counters()).Held.Should().Be(0, "admission takes no reserve commitment");
    }

    [Fact]
    public async Task GrantRevocationRacingFirstEgressNeverLeavesAnAuthorizedSendWithoutTheAuthorityItChecked()
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        source = (await _store.CommitExchangeAsync(source.Id))!;
        source = (await _store.BeginExchangePreparationAsync(source.Id, source.Version))!;
        source = (await _store.SaveExchangePreparedAsync(source.Id, source.Version, Wire(source)))!;

        var authorize = _store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version);
        var revoke = ChangeAuthorityAsync("participant-grant", intent.ApiKeyId, authority.Grant.Id, authority.Wallet.Id, authority.ParticipantUserId);
        await Task.WhenAll(authorize, revoke).WaitAsync(TimeSpan.FromSeconds(15));

        var persisted = (await _store.GetExchangeSourceAsync(source.Id))!;
        if (authorize.Result is null)
            persisted.State.Should().Be("Prepared", "a refused first egress leaves the saved wire unsent");
        else
            persisted.State.Should().Be("Submitted", "an authorization that committed did so before the revocation");
        (await _store.AuthorizeExchangeSubmissionAsync(source.Id, persisted.Version)).Should().BeNull(
            "after revocation no later attempt can authorize a send");
    }

    private async Task ChangeAuthorityAsync(string change, Guid apiKey, Guid grant, Guid wallet, Guid participant)
    {
        if (change == "tenant-key")
            await Execute(SurrealWriter.UpdateOnly<ApiKey>(apiKey.ToString("N")).Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        else if (change == "participant-grant")
            await Execute(SurrealWriter.UpdateOnly<ConsentGrant>(grant.ToString("N")).Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        else if (change == "participant-wallet")
            await Execute(SurrealWriter.UpdateOnly<Wallet>(wallet.ToString("N")).Set(row => row.Address, "rebound").Build());
        else
            await Execute(SurrealWriter.UpdateOnly<User>(participant.ToString("N")).Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build());
    }
}
