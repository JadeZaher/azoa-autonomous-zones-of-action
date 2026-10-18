using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task FullyAttributedReversalRecoversTheWholeSettledPurchaseOnce()
    {
        var (source, intent) = await SettledHeldPurchaseAsync("none");
        var request = Recovery(intent, source, "reversal-1", "lot-1", want: "10", claim: "10");

        var results = await Task.WhenAll(_store.RecoverExchangePurchaseAsync(request), _store.RecoverExchangePurchaseAsync(request))
            .WaitAsync(TimeSpan.FromSeconds(15));

        var step = results.OfType<ExchangePurchaseRecoveryResult>().DistinctBy(result => result.ReceiptId).Should().ContainSingle(
            "a concurrent duplicate either replays the winner's step or finds nothing more due").Subject;
        step.RecoveredHoldUnits.Should().Be("400");
        step.CreditedSourceUnits.Should().Be("10");
        step.RemainderRecordId.Should().BeNull();
        (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("Refunded");
        (await Counters()).Should().Be(ReservePartitionCounters.Declaring(1000));
        (await new SurrealHoldReleaseMandateStore(_executor).GetAsync(MandateOf(source)))!.Status.Should().Be("Cancelled");
        (await _store.ListReversalRecoveriesAsync("reversal-1", intent.ParticipantUserId)).Should().ContainSingle();
        (await _store.RecoverExchangePurchaseAsync(request with { ReversalId = "reversal-2" })).Should().BeNull(
            "the lot's whole attributed share is already consumed");
    }

    [Fact]
    public async Task RepeatedAndChangedPassesNeverRecoverMoreThanTheLotsAttributedShare()
    {
        var (source, intent) = await SettledHeldPurchaseAsync("none");

        var first = (await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-1", "lot-1", want: "4", claim: "6")))!;
        var replay = await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-1", "lot-1", want: "4", claim: "6"));
        var grown = (await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-1", "lot-1", want: "6", claim: "6")))!;
        var again = await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-1", "lot-1", want: "6", claim: "6"));
        var otherReversal = await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-2", "lot-1", want: "3", claim: "6"));

        first.CreditedSourceUnits.Should().Be("4");
        replay.Should().BeNull("a pass whose row save was lost asks again for what it already took");
        grown.CreditedSourceUnits.Should().Be("2", "only the growth beyond what this reversal already took is due");
        again.Should().BeNull();
        otherReversal.Should().BeNull("a second reversal of the same lot cannot exceed the lot's share of this purchase");
        var allocation = (await _executor.QueryAsync<ExchangePurchaseAllocation>(SurrealQuery<ExchangePurchaseAllocation>.From())).Single();
        allocation.ConsumedSourceUnits.Should().Be("6");
        allocation.RecoveredHoldUnits.Should().Be("240", "6 of 10 platform units funded 60 percent of the 400 purchased units");
        (await Counters()).Held.Should().Be(160);
    }

    [Fact]
    public async Task PartialAttributionSplitsTheLiabilityAndKeepsANarrowedReleaseForTheRemainder()
    {
        var (source, intent) = await SettledHeldPurchaseAsync("none");
        var first = (await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-1", "lot-1", want: "4", claim: "10")))!;

        first.RecoveredHoldUnits.Should().Be("160", "4 of 10 platform units funded 40 percent of the 400 purchased units");
        var parent = (await _store.GetHoldAsync(source.HoldRecordId!))!;
        parent.State.Should().Be("PartiallyRecovered");
        var remainder = (await _store.GetHoldAsync(first.RemainderRecordId!))!;
        remainder.State.Should().Be("Held");
        remainder.Amount.Should().Be("240");
        remainder.EconomicSourceId.Should().Be(source.Id);
        remainder.ParentHoldId.Should().Be(parent.HoldId);
        (await Counters()).Held.Should().Be(240);
        var narrowed = (await new SurrealHoldReleaseMandateStore(_executor).GetAsync(remainder.ActiveReleaseMandateId!))!;
        narrowed.Status.Should().Be("Active");
        (await new SurrealHoldReleaseMandateStore(_executor).GetAsync(MandateOf(source)))!.Status.Should().Be("Cancelled");

        var release = intent.Opening! with { RequestKey = "mandate:" + narrowed.Id, Kind = "Release", HoldId = remainder.HoldId, Definition = null };
        var quote = intent.ReleaseConsent!.Quote;
        quote = quote with { Request = quote.Request with { QuantityBaseUnits = "240" }, QuoteId = string.Empty };
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(release), 0, quote, null, intent.ParticipantWallet,
            SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TransferSign });
        (await _store.ClaimRailAsync(release, plan, new(1, ReleaseMandateId: narrowed.Id)))!.State.Should().Be("Admitted",
            "the remainder releases to the same recipient under the same caps, now for exactly what is still held");

        var second = await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-2", "lot-1", want: "4", claim: "10"));
        second.Should().BeNull("a remainder already being released is not recoverable from under the release");
        var allocation = (await _executor.QueryAsync<ExchangePurchaseAllocation>(SurrealQuery<ExchangePurchaseAllocation>.From())).Single();
        allocation.ConsumedSourceUnits.Should().Be("4");
        allocation.RecoveredHoldUnits.Should().Be("160");
    }

    [Theory]
    [InlineData("3", "10", new[] { "1", "1", "1" }, new[] { "4", "4", "2" })]
    [InlineData("1000", "1", new[] { "500" }, new[] { "1" })]
    public async Task RecoveryRoundsUpWithinWhatIsStillHeld(string spent, string purchased, string[] reversals, string[] recovered)
    {
        var (source, intent) = await SettledHeldPurchaseAsync(spent, purchased);

        var steps = new List<ExchangePurchaseRecoveryResult>();
        for (var index = 0; index < reversals.Length; index++)
            steps.Add((await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, $"reversal-{index}", $"lot-{index}",
                want: reversals[index], claim: reversals.Length == 1 ? spent : reversals[index])))!);

        steps.Select(step => step.RecoveredHoldUnits).Should().Equal(recovered,
            "rounding down would leave the platform a fraction out of pocket on every partial reversal");
        steps.Select(step => step.CreditedSourceUnits).Should().Equal(reversals);
        (await Counters()).Should().Be(ReservePartitionCounters.Declaring(1000), "the whole purchase came back, never more");
    }

    [Fact]
    public async Task ReversalOfAnotherPersonsPurchaseRecoversNothing()
    {
        var (source, intent) = await SettledHeldPurchaseAsync("none");

        (await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-1", "lot-1", "10", "10") with { SubjectUserId = Guid.NewGuid() }))
            .Should().BeNull("only the purchase's own beneficiary's reversed payment may unwind it");

        (await Counters()).Held.Should().Be(400);
        (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("Held");
    }

    [Fact]
    public async Task AReleasedPurchaseNamesWhoReceivedItSoAReversalCanClawItBack()
    {
        var (source, intent) = await SettledHeldPurchaseAsync("10", "400");
        var hold = (await _store.GetHoldAsync(source.HoldRecordId!))!;
        (await _store.GetPurchaseLiabilityAsync(source.Id))!.State.Should().Be("Held");
        var now = DateTimeOffset.UtcNow;
        var quote = new ValueCustodyQuote(1, "quote", new("Algorand", ChainNetwork.Devnet, "42", "reserve", "recipient-wallet", "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5));
        var recipient = new SigningWalletWitness(Guid.NewGuid(), intent.ParticipantUserId, "recipient-wallet", "Algorand");
        var plan = new HoldRailPlan(1, "Release", "release-command", 0, quote, null, recipient, SigningContext.PlatformFor("Algorand"));
        await Save(new HoldCommand { Id = "release-command", ActorUserId = intent.ParticipantUserId.ToString("N"),
            HoldRecordId = SurrealRecordGuid.BareId(hold.Id), Kind = "Release", State = "Completed", IntentHash = "release",
            IntentJson = "{}", AuthorityJson = "{}", RailPlanJson = System.Text.Json.JsonSerializer.Serialize(plan), CreatedAt = now, UpdatedAt = now });
        await Execute(SurrealWriter.UpdateOnly<HoldLedger>(hold.Id).Where(seed => true).Set(row => row.State, "Released").Build());

        var liability = (await _store.GetPurchaseLiabilityAsync(source.Id))!;

        liability.State.Should().Be("Released");
        liability.ReleasedToAddress.Should().Be("recipient-wallet");
        liability.QuantityBaseUnits.Should().Be("400");
        (await _store.RecoverExchangePurchaseAsync(Recovery(intent, source, "reversal-1", "lot-1", "10", "10"))).Should().BeNull(
            "released units have left custody; only a clawback from the recipient can recover them");
        var clawback = (await _store.ReserveExchangeClawbackAsync(Recovery(intent, source, "reversal-1", "lot-1", "10", "10")))!;
        clawback.Clawback.Should().BeTrue();
        clawback.RecoveredHoldUnits.Should().Be("400", "never more than the release delivered");
    }

    [Fact]
    public async Task ClawbacksOfADirectBuyTakeEachRefundsShareAndNeverMoreThanWasDelivered()
    {
        var (intent, authority) = await ExchangeUnitsAsync("Issue");
        var (source, settled) = await SettleAsync(intent with { Units = intent.Units! with { QuantityBaseUnits = "1000" } }, authority);

        var first = (await _store.ReserveExchangeClawbackAsync(Recovery(settled, source, "reversal-1", "lot-1", want: "3", claim: "10")))!;
        var second = (await _store.ReserveExchangeClawbackAsync(Recovery(settled, source, "reversal-1", "lot-1", want: "6", claim: "10")))!;
        var replay = await _store.ReserveExchangeClawbackAsync(Recovery(settled, source, "reversal-1", "lot-1", want: "6", claim: "10"));
        var rest = (await _store.ReserveExchangeClawbackAsync(Recovery(settled, source, "reversal-2", "lot-1", want: "10", claim: "10")))!;
        var beyond = await _store.ReserveExchangeClawbackAsync(Recovery(settled, source, "reversal-3", "lot-1", want: "10", claim: "10"));

        first.RecoveredHoldUnits.Should().Be("300", "a 30 percent refund claws 30 percent of the 1000 delivered units, not all of them");
        first.CreditedSourceUnits.Should().Be("3");
        second.RecoveredHoldUnits.Should().Be("300", "the second 30 percent claws the next 300");
        replay.Should().BeNull("a repeated pass reserves nothing new; it finishes or replays the reservation it already made");
        rest.RecoveredHoldUnits.Should().Be("400");
        beyond.Should().BeNull("all 1000 delivered units are already clawed");
        (await _store.ListReversalRecoveriesAsync("reversal-1", settled.ParticipantUserId)).Should().HaveCount(2)
            .And.OnlyContain(step => step.Clawback);
        (await _executor.QueryAsync<ExchangePurchaseAllocation>(SurrealQuery<ExchangePurchaseAllocation>.From())).Single()
            .ClawedAssetUnits.Should().Be("1000");
    }

    [Fact]
    public async Task ConcurrentCommitmentsOnOneReserveRowAllCommitAfterReplanning()
    {
        await SeedTreasuryAsync();
        var sources = new List<ExchangeHoldSource>();
        foreach (var key in new[] { "contended-1", "contended-2", "contended-3" })
        {
            var (intent, authority) = await ExchangeUnitsAsync("Issue", key);
            sources.Add((await _store.AdmitExchangeSourceAsync(intent with { Units = intent.Units! with { QuantityBaseUnits = "100" } },
                authority))!);
        }

        var committed = await Task.WhenAll(sources.Select(source => _store.CommitExchangeAsync(source.Id)))
            .WaitAsync(TimeSpan.FromSeconds(30));

        committed.Should().OnlyContain(source => source != null && source.State == "Committed",
            "another commitment moving the shared reserve row is contention to re-plan, not a refusal");
        (await Counters()).Held.Should().Be(300);
    }

    private static ExchangePurchaseRecovery Recovery(ExchangeHoldSourceIntent intent, ExchangeHoldSource source, string reversal,
        string lot, string want, string claim)
        => new(1, reversal, lot, intent.ParticipantUserId, source.Id, want, "Card payment reversed.", claim);

    private async Task<(ExchangeHoldSource Source, ExchangeHoldSourceIntent Intent)> SettledHeldPurchaseAsync(string change)
    {
        var (intent, authority) = await ExchangeMandateAsync(change);
        return await SettleAsync(intent, authority);
    }

    /// <summary>A settled held purchase with no release mandate and the given spent/purchased quantities.</summary>
    private async Task<(ExchangeHoldSource Source, ExchangeHoldSourceIntent Intent)> SettledHeldPurchaseAsync(string spent, string purchased)
    {
        var (intent, authority) = await ExchangeAsync();
        var opening = intent.Opening! with { Definition = intent.Opening!.Definition! with { QuantityBaseUnits = purchased } };
        return await SettleAsync(intent with { PlatformQuantityBaseUnits = spent, Opening = opening }, authority);
    }

    private async Task<(ExchangeHoldSource Source, ExchangeHoldSourceIntent Intent)> SettleAsync(ExchangeHoldSourceIntent intent,
        ExchangeParticipantAuthority authority)
    {
        await SeedTreasuryAsync();
        var source = await SubmittedAsync(intent, authority);
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, Verdict(source, "Confirmed"));
        await _store.CompleteExchangeSettlementAsync(source.Id);
        return ((await _store.GetExchangeSourceAsync(source.Id))!, intent);
    }

    private static string MandateOf(ExchangeHoldSource source)
        => CanonicalJson.Hash(new { Domain = "exchange-release-mandate-v1", source.Id });
}
