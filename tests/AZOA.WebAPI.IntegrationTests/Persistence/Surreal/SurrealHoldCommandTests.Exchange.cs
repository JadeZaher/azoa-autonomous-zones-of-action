using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task ExchangeAdmissionHasNoLiabilityAndConcurrentCommitConsumesSourceOnce()
    {
        var (intent, authority) = await ExchangeAsync();
        var source = await _store.AdmitExchangeSourceAsync(intent, authority);
        source.Should().NotBeNull();
        (await Counters()).Held.Should().Be(0);
        (await _store.GetHoldAsync(HoldCommandIdentity.Hold(intent.Opening!))).Should().BeNull();

        var receipts = await Task.WhenAll(_store.CommitExchangeOpeningAsync(source!.Id),
            _store.CommitExchangeOpeningAsync(source.Id)).WaitAsync(TimeSpan.FromSeconds(15));

        receipts.Should().OnlyContain(receipt => receipt != null);
        receipts[0]!.OutcomeJson.Should().Be(receipts[1]!.OutcomeJson);
        var persisted = (await _store.GetExchangeSourceAsync(source.Id))!;
        persisted.State.Should().Be("Committed");
        persisted.Version.Should().Be(1);
        persisted.HoldCommandId.Should().Be(HoldCommandIdentity.Operation(intent.Opening!));
        persisted.HoldRecordId.Should().Be(HoldCommandIdentity.Hold(intent.Opening!));
        var liability = (await _store.GetHoldAsync(persisted.HoldRecordId!))!;
        liability.State.Should().Be("AwaitingSourceSettlement", "unsettled purchase funds cannot unlock release or ordinary refund");
        liability.EconomicSourceId.Should().Be(source.Id);
        (await Counters()).Held.Should().Be(400);
        var outcome = JsonSerializer.Deserialize<HoldCommandView>(receipts[0]!.OutcomeJson)!;
        outcome.ActorUserId.Should().Be(_actor);
        outcome.BeneficiaryUserId.Should().Be(authority.ParticipantUserId);
        outcome.TenantId.Should().BeNull("the real API-key principal is not a fabricated child credential");
        outcome.CustodyTenantId.Should().Be(_actor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceCommitRechecksBothAuthoritiesButCompletedReceiptSurvivesRevocation(bool tenantKey)
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        if (tenantKey)
            await Execute(SurrealWriter.UpdateOnly<ApiKey>(intent.ApiKeyId.ToString("N"))
                .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        else
            await Execute(SurrealWriter.UpdateOnly<ConsentGrant>(authority.Grant.Id.ToString("N"))
                .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        (await _store.CommitExchangeOpeningAsync(source.Id)).Should().BeNull();
        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Admitted");
        (await Counters()).Held.Should().Be(0);

        var (another, live) = await ExchangeAsync("another");
        var admitted = (await _store.AdmitExchangeSourceAsync(another, live))!;
        var committed = (await _store.CommitExchangeOpeningAsync(admitted.Id))!;
        await Execute(SurrealWriter.UpdateOnly<ConsentGrant>(live.Grant.Id.ToString("N"))
            .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        (await _store.CommitExchangeOpeningAsync(admitted.Id))!.OutcomeJson.Should().Be(committed.OutcomeJson);
        (await Counters()).Held.Should().Be(400);
    }

    [Fact]
    public async Task ChangedOriginalRequestOrBeneficiaryCannotReuseSourceOrCommitAnotherLiability()
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        (await _store.AdmitExchangeSourceAsync(intent with { PlatformQuantityBaseUnits = "11" }, authority)).Should().BeNull();
        (await _store.AdmitExchangeSourceAsync(intent with { OriginalRequestJson = "{\"different\":true}" }, authority)).Should().BeNull();
        (await _store.AdmitExchangeSourceAsync(intent with { ParticipantUserId = Guid.NewGuid() }, authority)).Should().BeNull();
        (await _store.CommitExchangeOpeningAsync(source.Id)).Should().NotBeNull();
        var alternate = intent.Opening! with { RequestKey = "forged", HoldId = "forged" };
        (await _store.CommitLocalAsync(alternate, new(1, EconomicSource: new("Exchange", source.Id, source.IntentHash))))
            .Should().BeNull("the generic local API cannot manufacture source authority");
        (await _store.CommitLocalAsync(intent.Opening! with { RequestKey = "premature-refund", Kind = "Refund", Definition = null }, Authority()))
            .Should().BeNull("an ordinary owner cannot release an unresolved purchase commitment");
        (await Counters()).Held.Should().Be(400);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparedSourceCannotFirstSendAfterEitherOriginalAuthorityIsRevoked(bool tenantKey)
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        await _store.CommitExchangeOpeningAsync(source.Id);
        source = (await _store.GetExchangeSourceAsync(source.Id))!;
        source = (await _store.BeginExchangePreparationAsync(source.Id, source.Version))!;
        var wireIntent = JsonSerializer.Deserialize<ExchangeWireIntent>(source.WireIntentJson!)!;
        var prepared = new PreparedExchangeWire(wireIntent, "provider-preparation", "group", ["tx"], ["c2lnbmVk"]);
        source = (await _store.SaveExchangePreparedAsync(source.Id, source.Version, prepared))!;
        if (tenantKey)
            await Execute(SurrealWriter.UpdateOnly<ApiKey>(intent.ApiKeyId.ToString("N"))
                .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        else
            await Execute(SurrealWriter.UpdateOnly<ConsentGrant>(authority.Grant.Id.ToString("N"))
                .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        (await _store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version)).Should().BeNull();
        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Prepared");
        (await Counters()).Held.Should().Be(400);
    }

    [Fact]
    public async Task OnlyTheAuthorizedWireCanRecordEvidenceAfterOriginalCredentialsExpire()
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        await _store.CommitExchangeOpeningAsync(source.Id);
        source = (await _store.GetExchangeSourceAsync(source.Id))!;
        source = (await _store.BeginExchangePreparationAsync(source.Id, source.Version))!;
        var wireIntent = JsonSerializer.Deserialize<ExchangeWireIntent>(source.WireIntentJson!)!;
        var prepared = new PreparedExchangeWire(wireIntent, "provider-preparation", "group", ["tx"], ["c2lnbmVk"]);
        (await _store.SaveExchangePreparedAsync(source.Id, source.Version,
            prepared with { Intent = wireIntent with { MaximumNetworkFeeBaseUnits = "999999" } })).Should().BeNull();
        source = (await _store.SaveExchangePreparedAsync(source.Id, source.Version, prepared))!;
        var overlap = await Task.WhenAll(_store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version),
            _store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version)).WaitAsync(TimeSpan.FromSeconds(15));
        overlap.Count(row => row is not null).Should().Be(1);
        source = overlap.Single(row => row is not null)!;
        await Execute(SurrealWriter.UpdateOnly<ConsentGrant>(authority.Grant.Id.ToString("N"))
            .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        var evidence = new ExchangeWireObservation("Confirmed", prepared.PreparationId, prepared.GroupId,
            CanonicalJson.Hash(prepared), prepared.Intent.ProtocolIdentity, "100");
        (await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, evidence with { GroupId = "other" })).Should().BeNull();
        (await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, evidence)).Should().NotBeNull();
        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Confirmed");
        (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("AwaitingSourceSettlement",
            "chain finality and finalized accounting projection are distinct transitions");
        var treasuryId = ProjectTreasuryLedger.RecordIdFor("Algorand", "Devnet", _actor.ToString("N"), "project");
        await Save(new ProjectTreasuryLedger { Id = treasuryId, Chain = "Algorand", Network = "Devnet",
            TenantId = "user:" + _actor.ToString("N"), ProjectReference = "project", TreasuryAddress = "treasury",
            DerivationVersion = "fixture", Status = "Closed", Assets = ProjectTreasuryAssetCodec.Encode(new Dictionary<string, ProjectTreasuryAssetCounters>()) });
        var settled = await Task.WhenAll(_store.CompleteExchangeSettlementAsync(source.Id),
            _store.CompleteExchangeSettlementAsync(source.Id)).WaitAsync(TimeSpan.FromSeconds(15));
        settled.Should().OnlyContain(receipt => receipt != null);
        settled[0]!.PreparedHash.Should().Be(evidence.PreparedHash);
        (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("Held");
        var treasury = (await _executor.QuerySingleAsync<ProjectTreasuryLedger>(SurrealQuery<ProjectTreasuryLedger>.Key(treasuryId)))!;
        ProjectTreasuryAssetCodec.Decode(treasury.Assets, out _)!["7"].Credited.Should().Be(10);
        treasury.Status.Should().Be("Closed", "finalized accounting records actual received funds without reopening treasury authority");
        (await Counters()).Held.Should().Be(400);
    }

    [Fact]
    public async Task AbsentSourceReturnsOnlyItsOriginalCommitmentOnceAndNeverCreditsTreasury()
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        await _store.CommitExchangeOpeningAsync(source.Id);
        source = (await _store.GetExchangeSourceAsync(source.Id))!;
        source = (await _store.BeginExchangePreparationAsync(source.Id, source.Version))!;
        var wireIntent = JsonSerializer.Deserialize<ExchangeWireIntent>(source.WireIntentJson!)!;
        var prepared = new PreparedExchangeWire(wireIntent, "preparation", "group", ["tx"], ["c2lnbmVk"]);
        source = (await _store.SaveExchangePreparedAsync(source.Id, source.Version, prepared))!;
        source = (await _store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version))!;
        (await _store.CompensateExchangeAbsenceAsync(source.Id)).Should().BeNull("missing receipt is not proof of no chain effect");
        var evidence = new ExchangeWireObservation("ExpiredAbsent", prepared.PreparationId, prepared.GroupId,
            CanonicalJson.Hash(prepared), prepared.Intent.ProtocolIdentity);
        (await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, evidence with { PreparedHash = "another" })).Should().BeNull();
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, evidence);
        var results = await Task.WhenAll(_store.CompensateExchangeAbsenceAsync(source.Id),
            _store.CompensateExchangeAbsenceAsync(source.Id)).WaitAsync(TimeSpan.FromSeconds(15));
        results.Should().OnlyContain(receipt => receipt != null);
        results[0]!.TreasuryRecordId.Should().BeEmpty();
        (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("Cancelled");
        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Compensated");
        (await Counters()).Held.Should().Be(0);
        (await Counters()).Available.Should().Be(1000);
        (await _store.CompleteExchangeSettlementAsync(source.Id)).Should().BeNull();
    }

    private async Task<(ExchangeHoldSourceIntent Intent, ExchangeParticipantAuthority Authority)> ExchangeAsync(string key = "purchase")
    {
        var participant = Guid.NewGuid(); var wallet = Guid.NewGuid(); var apiKey = Guid.NewGuid(); var grant = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await Save(new User { Id = participant.ToString("N"), Username = participant.ToString("N"),
            Email = $"{participant:N}@example.test", PasswordHash = "fixture-no-login", IsActive = true });
        await Save(new ApiKey { Id = apiKey.ToString("N"), UserId = $"user:{_actor:N}", IsActive = true,
            KeyHash = new string('b', 64), KeyPrefix = "fixture", CreatedDate = now, Scopes = [AzoaScopes.SwapSign] });
        await Save(new Wallet { Id = wallet.ToString("N"), UserId = $"user:{participant:N}", ChainType = "Algorand", Address = "participant" });
        await Save(new ConsentGrant { Id = grant.ToString("N"), GrantorUserId = $"user:{participant:N}", TenantId = $"user:{_actor:N}",
            Origin = "UserExplicit", Scopes = AzoaScopes.SwapSign, GrantedAt = now });
        var credential = new SigningCredentialWitness(1, "ApiKey", _actor, null, null, null, null, apiKey,
            new string('b', 64), [AzoaScopes.SwapSign], false, [AzoaScopes.SwapSign]);
        var authority = new ExchangeParticipantAuthority(1, new(1, credential, [AzoaScopes.SwapSign], []), participant, null,
            new(wallet, participant, "participant", "Algorand"), new(grant, AzoaScopes.SwapSign, AzoaScopes.SwapSign, now, null));
        var opening = Open() with { RequestKey = "exchange:" + ExchangeHoldSourceIdentity.ForRequest(_actor, apiKey, key),
            HoldId = key, CustodyTenantId = _actor };
        opening = opening with { Definition = opening.Definition! with { BeneficiaryUserId = participant } };
        return (new(1, key, apiKey, _actor, participant, "{}", opening, "7", "10", "project", "treasury", authority.Wallet, "10000"), authority);
    }
}
