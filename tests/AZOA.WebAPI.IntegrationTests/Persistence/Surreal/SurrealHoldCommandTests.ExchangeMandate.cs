using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Theory]
    [InlineData("none", true)]
    [InlineData("scope", false)]
    [InlineData("expiry", false)]
    [InlineData("disclosure", false)]
    public async Task ExchangeAutoReleaseRequiresSeparateExactConsentInTheOpeningTransaction(string change, bool succeeds)
    {
        var (intent, authority) = await ExchangeMandateAsync(change);
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        var receipt = await _store.CommitExchangeOpeningAsync(source.Id);
        (receipt is not null).Should().Be(succeeds);
        (await Counters()).Held.Should().Be(succeeds ? 400 : 0);
        var mandates = await _executor.QueryAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.From());
        mandates.Count.Should().Be(succeeds ? 1 : 0);
        if (!succeeds) return;
        var hold = (await _store.GetHoldAsync(source.HoldRecordId ?? HoldCommandIdentity.Hold(intent.Opening)))!;
        hold.State.Should().Be("AwaitingSourceSettlement");
        hold.ActiveReleaseMandateId.Should().Be(mandates[0].Id);
        var view = JsonSerializer.Deserialize<HoldCommandView>(receipt!.OutcomeJson)!;
        view.ReleaseMandateId.Should().Be(mandates[0].Id);
        var release = intent.Opening with { RequestKey = "mandate:" + mandates[0].Id, Kind = "Release", Definition = null };
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(release), 0, intent.ReleaseConsent!.Quote,
            null, intent.ParticipantWallet, SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TransferSign });
        (await _store.ClaimRailAsync(release, plan, new(1, ReleaseMandateId: mandates[0].Id))).Should().BeNull();
    }

    [Fact]
    public async Task ProvenAbsentExchangeClosesItsOriginalMandateWithReserveReturn()
    {
        var (intent, authority) = await ExchangeMandateAsync("none");
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        await _store.CommitExchangeOpeningAsync(source.Id);
        source = (await _store.GetExchangeSourceAsync(source.Id))!;
        var hold = (await _store.GetHoldAsync(source.HoldRecordId!))!;
        var mandateId = hold.ActiveReleaseMandateId!;
        source = (await _store.BeginExchangePreparationAsync(source.Id, source.Version))!;
        var wire = new PreparedExchangeWire(JsonSerializer.Deserialize<ExchangeWireIntent>(source.WireIntentJson!)!,
            "preparation", "group", ["tx"], ["c2lnbmVk"]);
        source = (await _store.SaveExchangePreparedAsync(source.Id, source.Version, wire))!;
        source = (await _store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version))!;
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version,
            new("ExpiredAbsent", wire.PreparationId, wire.GroupId, CanonicalJson.Hash(wire), wire.Intent.ProtocolIdentity));
        (await _store.CompensateExchangeAbsenceAsync(source.Id)).Should().NotBeNull();
        var mandate = (await new SurrealHoldReleaseMandateStore(_executor).GetAsync(mandateId))!;
        mandate.Status.Should().Be("Cancelled");
        (await _store.GetHoldAsync(hold.Id))!.ActiveReleaseMandateId.Should().BeNull();
        (await Counters()).Held.Should().Be(0);
        (await _store.CompensateExchangeAbsenceAsync(source.Id)).Should().NotBeNull();
        (await new SurrealHoldReleaseMandateStore(_executor).GetAsync(mandateId))!.Version.Should().Be(mandate.Version);
    }

    private async Task<(ExchangeHoldSourceIntent Intent, ExchangeParticipantAuthority Authority)> ExchangeMandateAsync(string change)
    {
        var (intent, authority) = await ExchangeAsync();
        var now = DateTimeOffset.UtcNow;
        var scopes = change == "scope" ? new[] { AzoaScopes.SwapSign } : [AzoaScopes.SwapSign, AzoaScopes.TransferSign];
        await Execute(SurrealWriter.UpdateOnly<ApiKey>(intent.ApiKeyId.ToString("N")).Set(row => row.Scopes, scopes).Build());
        var credential = authority.Tenant.Credential with { Scopes = scopes, StoredApiKeyScopes = scopes };
        authority = authority with { Tenant = authority.Tenant with { Credential = credential } };
        var opening = intent.Opening with { Definition = intent.Opening.Definition! with { AutoRelease = true, ReleaseNotBefore = now.AddMinutes(-1) } };
        var costs = new ValueCustodyCostBreakdown("reserve", "reserve", "1000", "0", "0");
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", "reserve", "participant", "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0",
            change == "expiry" ? now.AddMinutes(-10) : now, change == "expiry" ? now.AddMinutes(-5) : now.AddMinutes(5), costs);
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var terms = new HoldReleaseMandateTerms(1, HoldCommandIdentity.Hold(opening), CanonicalJson.Hash(opening.Definition!), opening.Asset,
            "400", intent.ParticipantWallet, opening.Definition!.ReleaseNotBefore!.Value, now.AddDays(1), "ALGO", 6, costs);
        var hash = change == "disclosure" ? "changed" : CanonicalJson.Hash(new { Terms = terms, Quote = quote });
        var consent = new ExchangeReleaseConsent(Guid.NewGuid(), terms, quote, new(1, credential, [AzoaScopes.TransferSign], []), hash);
        return (intent with { Opening = opening, ReleaseConsent = consent }, authority);
    }
}
