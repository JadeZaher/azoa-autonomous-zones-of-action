using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task OwnerCannotLocallyRefundAPurchasedLiability()
    {
        var (intent, authority) = await ExchangeAsync();
        await SeedTreasuryAsync();
        var source = await SubmittedAsync(intent, authority);
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, Verdict(source, "Confirmed"));
        (await _store.CompleteExchangeSettlementAsync(source.Id)).Should().NotBeNull();
        var owner = Authority();
        var command = intent.Opening! with { RequestKey = "local-refund", Kind = "Refund", Definition = null };

        (await _store.CommitLocalAsync(command, owner)).Should().BeNull(
            "the participant paid for these units; only their release, the source's proven absence or an exact reversal settles them");

        (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("Held");
        (await Counters()).Held.Should().Be(400);
    }

    [Fact]
    public async Task OwnerRefundOfAHoldWithAnUnexercisedReleaseMandateClosesTheMandateAtomically()
    {
        var open = Open(); var authority = Authority();
        (await _store.CommitLocalAsync(open, authority)).Should().NotBeNull();
        var hold = (await _store.GetHoldAsync(HoldCommandIdentity.Hold(open)))!;
        var recipient = new SigningWalletWitness(Guid.NewGuid(), _actor, "recipient", "Algorand");
        await Save(new Wallet { Id = recipient.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = recipient.Address, ChainType = recipient.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        var now = DateTimeOffset.UtcNow;
        var costs = new ValueCustodyCostBreakdown("reserve", "reserve", "1000", "0", "0");
        var quote = new ValueCustodyQuote(1, "", new("Algorand", open.Asset.Network, "42", "reserve", "recipient", "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5), costs);
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var terms = new HoldReleaseMandateTerms(1, hold.Id, hold.DefinitionHash!, open.Asset, "400", recipient,
            hold.ReleaseNotBefore!.Value, hold.ReleaseNotBefore.Value.AddDays(1), "ALGO", 6, costs);
        var mandates = new SurrealHoldReleaseMandateStore(_executor);
        var offer = (await mandates.OfferAsync(new(Guid.NewGuid(), hold.Version, terms, quote), authority.Direct!))!;
        var accepted = (await mandates.AdmitAsync(new(offer.Id, offer.RequestHash), authority.Direct!))!;

        var refund = open with { RequestKey = "refund", Kind = "Refund", Definition = null };
        (await _store.CommitLocalAsync(refund, authority)).Should().NotBeNull();

        (await mandates.GetAsync(accepted.Id))!.Status.Should().Be("Cancelled", "a refunded liability can never be auto-released");
        var settled = (await _store.GetHoldAsync(hold.Id))!;
        settled.State.Should().Be("Refunded");
        settled.ActiveReleaseMandateId.Should().BeNull();
        (await Counters()).Held.Should().Be(0);
    }
}
