using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task DirectPurchaseHoldsPartitionAtCommitAndIssuesWithOneTreasuryCreditOnSettlement()
    {
        var (intent, authority) = await ExchangeUnitsAsync("Issue");
        await SeedTreasuryAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        (await Counters()).Held.Should().Be(0);

        source = (await _store.CommitExchangeAsync(source.Id))!;
        source.State.Should().Be("Committed");
        source.HoldRecordId.Should().BeNull("a direct delivery creates no custody liability");
        (await Counters()).Held.Should().Be(400, "the units are committed before any signature exists");

        source = await SubmittedAsync(intent, authority);
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, Verdict(source, "Confirmed"));
        var settled = await Task.WhenAll(_store.CompleteExchangeSettlementAsync(source.Id),
            _store.CompleteExchangeSettlementAsync(source.Id)).WaitAsync(TimeSpan.FromSeconds(15));

        settled.Should().OnlyContain(receipt => receipt != null);
        var counters = await Counters();
        counters.Held.Should().Be(0);
        counters.Issued.Should().Be(400);
        (await TreasuryCounters()).Credited.Should().Be(10, "the project is credited exactly once for the finalized funds leg");
        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Completed");
    }

    [Fact]
    public async Task ProvenAbsentDirectPurchaseReturnsOnlyItsPartitionCommitmentOnce()
    {
        var (intent, authority) = await ExchangeUnitsAsync("Issue");
        await SeedTreasuryAsync();
        var source = await SubmittedAsync(intent, authority);
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, Verdict(source, "ExpiredAbsent"));

        var results = await Task.WhenAll(_store.CompensateExchangeAbsenceAsync(source.Id),
            _store.CompensateExchangeAbsenceAsync(source.Id)).WaitAsync(TimeSpan.FromSeconds(15));

        results.Should().OnlyContain(receipt => receipt != null);
        (await Counters()).Should().Be(ReservePartitionCounters.Declaring(1000));
        (await TreasuryCounters()).Credited.Should().Be(0);
        (await _store.CompleteExchangeSettlementAsync(source.Id)).Should().BeNull("an absent group can never also settle");
    }

    [Fact]
    public async Task SaleHoldsTreasuryFundsAtCommitAndRecordsReturnedUnitsOnlyOnSettlement()
    {
        var (intent, authority) = await ExchangeUnitsAsync("Return");
        await SeedTreasuryAsync(credited: 25);
        await SetContributorsAsync(ReservePartitionCounters.Declaring(1000) with { Issued = 600 });
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;

        source = (await _store.CommitExchangeAsync(source.Id))!;
        (await TreasuryCounters()).Held.Should().Be(10, "the project's payout is reserved before the participant signs");
        (await Counters()).Returned.Should().Be(0, "units are not counted back until the sale lands");

        source = await SubmittedAsync(intent, authority);
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, Verdict(source, "Confirmed"));
        (await _store.CompleteExchangeSettlementAsync(source.Id)).Should().NotBeNull();

        var treasury = await TreasuryCounters();
        treasury.Held.Should().Be(0);
        treasury.Spent.Should().Be(10);
        (await Counters()).Returned.Should().Be(400);
    }

    [Theory]
    [InlineData(5, 600)]
    [InlineData(25, 100)]
    public async Task SaleWithoutTreasuryFundsOrIssuedUnitsCommitsNothing(long credited, long issued)
    {
        var (intent, authority) = await ExchangeUnitsAsync("Return");
        await SeedTreasuryAsync(credited: credited);
        await SetContributorsAsync(ReservePartitionCounters.Declaring(1000) with { Issued = issued });
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;

        (await _store.CommitExchangeAsync(source.Id)).Should().BeNull();

        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Admitted");
        (await TreasuryCounters()).Held.Should().Be(0);
    }

    [Fact]
    public async Task ProvenAbsentSaleReleasesTheTreasuryHold()
    {
        var (intent, authority) = await ExchangeUnitsAsync("Return");
        await SeedTreasuryAsync(credited: 25);
        await SetContributorsAsync(ReservePartitionCounters.Declaring(1000) with { Issued = 600 });
        var source = await SubmittedAsync(intent, authority);
        await _store.RecordExchangeEvidenceAsync(source.Id, source.Version, Verdict(source, "ExpiredAbsent"));

        (await _store.CompensateExchangeAbsenceAsync(source.Id)).Should().NotBeNull();

        var treasury = await TreasuryCounters();
        treasury.Held.Should().Be(0);
        treasury.Spent.Should().Be(0);
        (await Counters()).Returned.Should().Be(0);
    }

    [Fact]
    public async Task DirectPurchaseOfAnotherTenantsRegisteredUnitsCommitsNothing()
    {
        var (intent, authority) = await ExchangeUnitsAsync("Issue");
        var row = (await _executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(ReserveId)))!;
        await Execute(SurrealWriter.UpdateOnly<PlatformReserveLedger>(ReserveId).Where(value => value.Version == row.Version)
            .Set(value => value.TenantId, "user:" + Guid.NewGuid().ToString("N")).Build());
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;

        (await _store.CommitExchangeAsync(source.Id)).Should().BeNull("a tenant cannot draw another tenant's declared budget");
        (await Counters()).Held.Should().Be(0);
    }

    [Theory]
    [InlineData("Issue")]
    [InlineData("Hold")]
    public async Task StaleNeverSentCommitmentIsAbandonedOnceAndAFreshOneIsLeftAlone(string kind)
    {
        var (intent, authority) = kind == "Hold" ? await ExchangeAsync() : await ExchangeUnitsAsync(kind);
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        source = (await _store.CommitExchangeAsync(source.Id))!;

        (await _store.AbandonUnsentExchangeAsync(source.Id, source.UpdatedAt.AddSeconds(-1))).Should().BeNull(
            "a commitment still inside its window may belong to a request that is about to prepare");
        var abandoned = await Task.WhenAll(_store.AbandonUnsentExchangeAsync(source.Id, DateTimeOffset.UtcNow.AddMinutes(1)),
            _store.AbandonUnsentExchangeAsync(source.Id, DateTimeOffset.UtcNow.AddMinutes(1))).WaitAsync(TimeSpan.FromSeconds(15));

        abandoned.Should().OnlyContain(receipt => receipt != null);
        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Abandoned");
        (await Counters()).Should().Be(ReservePartitionCounters.Declaring(1000));
        if (kind == "Hold") (await _store.GetHoldAsync(source.HoldRecordId!))!.State.Should().Be("Cancelled");
        (await _store.BeginExchangePreparationAsync(source.Id, source.Version + 1)).Should().BeNull();
    }

    [Fact]
    public async Task AbandonmentCannotWinAfterFirstEgressWasAuthorized()
    {
        var (intent, authority) = await ExchangeUnitsAsync("Issue");
        var source = await SubmittedAsync(intent, authority);

        (await _store.AbandonUnsentExchangeAsync(source.Id, DateTimeOffset.UtcNow.AddMinutes(1))).Should().BeNull();

        (await _store.GetExchangeSourceAsync(source.Id))!.State.Should().Be("Submitted");
        (await Counters()).Held.Should().Be(400, "possibly-sent value keeps its commitment until authoritative evidence");
    }
}
