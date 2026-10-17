using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.TenantPayout;

/// <summary>D-8 tenant payout ledger on real SurrealDB: one float version admits one transition, so a balance covers what it covers exactly once; see Round4Gates/AGENTS.md §tenant-payout.</summary>
public sealed class TenantPayoutLedgerRaceTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private SurrealTenantPayoutLedger _ledger = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("tenant_payout_float", "tenant_payout_credit", "tenant_payout_debit");
        _ledger = new SurrealTenantPayoutLedger(_database.Executor, TimeProvider.System);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Two_concurrent_reservations_against_a_balance_that_covers_one_commit_exactly_one()
    {
        var funded = await FundedFloatAsync(10_000);
        var reservations = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => _ledger.CommitAsync(new TenantPayoutTransition(
            funded, new TenantPayoutCounters(funded.CreditedMinorUnits, funded.ReservedMinorUnits + 7_000, funded.FinalizedMinorUnits),
            new TenantPayoutEntryChange.CreateDebit(Debit(funded, 7_000))))));

        reservations.Count(committed => committed).Should().Be(1, "both racers read the same float version; only one may spend it");
        var snapshot = (await _ledger.ReadSnapshotAsync(funded.Id))!;
        snapshot.Float.ReservedMinorUnits.Should().Be(7_000);
        snapshot.Float.Available().Should().Be(3_000, "the loser re-reads 3,000 available and must refuse its 7,000 before any send");
        snapshot.Debits.Should().ContainSingle().Which.AmountMinorUnits.Should().Be(7_000);
        snapshot.Float.Version.Should().Be(funded.Version + 1);
    }

    [Fact]
    public async Task A_replayed_reservation_for_the_same_payout_never_reserves_twice()
    {
        var funded = await FundedFloatAsync(10_000);
        var debit = Debit(funded, 4_000);
        (await _ledger.CommitAsync(new TenantPayoutTransition(funded,
            new TenantPayoutCounters(funded.CreditedMinorUnits, 4_000, 0), new TenantPayoutEntryChange.CreateDebit(debit)))).Should().BeTrue();

        // Replay from a fresh read: the float CAS passes, the debit id (payout id) collides, the whole batch rolls back.
        var current = (await _ledger.GetFloatAsync(funded.Id))!;
        (await _ledger.CommitAsync(new TenantPayoutTransition(current,
            new TenantPayoutCounters(current.CreditedMinorUnits, current.ReservedMinorUnits + 4_000, 0),
            new TenantPayoutEntryChange.CreateDebit(Debit(funded, 4_000, debit.PayoutId))))).Should().BeFalse();

        var snapshot = (await _ledger.ReadSnapshotAsync(funded.Id))!;
        snapshot.Float.ReservedMinorUnits.Should().Be(4_000);
        snapshot.Float.Version.Should().Be(current.Version, "a rolled-back replay leaves no counter change behind");
        snapshot.Debits.Should().ContainSingle();
    }

    [Fact]
    public async Task Credit_promotion_racing_a_reversal_of_the_same_pending_credit_has_one_winner()
    {
        var empty = await _ledger.GetOrCreateFloatAsync(_tenant, "usd", "test");
        var credit = await _ledger.RecordCreditAsync(Credit(empty, net: 5_000));
        var promote = new TenantPayoutTransition(empty, new TenantPayoutCounters(5_000, 0, 0),
            new TenantPayoutEntryChange.UpdateCredit(credit.Id, credit.Version, TenantPayoutCreditStates.Pending, TenantPayoutCreditStates.Available, 0));
        var reverse = new TenantPayoutTransition(empty, new TenantPayoutCounters(0, 0, 0),
            new TenantPayoutEntryChange.UpdateCredit(credit.Id, credit.Version, TenantPayoutCreditStates.Pending, TenantPayoutCreditStates.Pending, 5_000));

        var outcomes = await Task.WhenAll(_ledger.CommitAsync(promote), _ledger.CommitAsync(reverse));

        outcomes.Count(committed => committed).Should().Be(1);
        var snapshot = (await _ledger.ReadSnapshotAsync(empty.Id))!;
        var stored = snapshot.Credits.Should().ContainSingle().Subject;
        if (outcomes[0])
        {
            stored.State.Should().Be(TenantPayoutCreditStates.Available);
            snapshot.Float.CreditedMinorUnits.Should().Be(5_000);
        }
        else
        {
            stored.ReversedMinorUnits.Should().Be(5_000, "the reversal won: the charged-back top-up never counted");
            snapshot.Float.CreditedMinorUnits.Should().Be(0);
        }
        snapshot.Float.CreditedMinorUnits.Should().Be(snapshot.Credits.Where(entry => entry.State == TenantPayoutCreditStates.Available).Sum(entry => entry.Contribution()),
            "balance = credits − finalized − reserved holds against the entry rows");
    }

    [Fact]
    public async Task A_credit_for_the_same_funding_intent_is_recorded_once()
    {
        var empty = await _ledger.GetOrCreateFloatAsync(_tenant, "usd", "test");
        var candidate = Credit(empty, net: 2_500);
        var recorded = await Task.WhenAll(_ledger.RecordCreditAsync(candidate), _ledger.RecordCreditAsync(Credit(empty, net: 9_999, intentId: candidate.IntentId)));

        recorded.Select(credit => credit.Id).Distinct().Should().ContainSingle();
        recorded.Should().OnlyContain(credit => credit.NetMinorUnits == recorded[0].NetMinorUnits, "the loser receives the winner's row, never its own amount");
        (await _ledger.ReadSnapshotAsync(empty.Id))!.Credits.Should().ContainSingle();
    }

    private async Task<TenantPayoutFloat> FundedFloatAsync(long amount)
    {
        var empty = await _ledger.GetOrCreateFloatAsync(_tenant, "usd", "test");
        var credit = await _ledger.RecordCreditAsync(Credit(empty, amount));
        (await _ledger.CommitAsync(new TenantPayoutTransition(empty, new TenantPayoutCounters(amount, 0, 0),
            new TenantPayoutEntryChange.UpdateCredit(credit.Id, credit.Version, TenantPayoutCreditStates.Pending, TenantPayoutCreditStates.Available, 0)))).Should().BeTrue();
        return (await _ledger.GetFloatAsync(empty.Id))!;
    }

    private static TenantPayoutCredit Credit(TenantPayoutFloat target, long net, string? intentId = null)
    {
        var intent = intentId ?? Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        return new TenantPayoutCredit
        {
            Id = TenantPayoutCredit.RecordIdFor(intent), IntentId = intent, FloatId = target.Id, TenantId = target.TenantId,
            CurrencyCode = target.CurrencyCode, Environment = target.Environment, PaymentIntentId = "pi_" + intent,
            GrossMinorUnits = net, NetMinorUnits = net, State = TenantPayoutCreditStates.Pending, AvailableAt = now, CreatedAt = now, UpdatedAt = now,
        };
    }

    private static TenantPayoutDebit Debit(TenantPayoutFloat target, long amount, string? payoutId = null)
    {
        var payout = payoutId ?? Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        return new TenantPayoutDebit
        {
            Id = payout, PayoutId = payout, FloatId = target.Id, TenantId = target.TenantId, CurrencyCode = target.CurrencyCode,
            Environment = target.Environment, AmountMinorUnits = amount, State = TenantPayoutDebitStates.Reserved, CreatedAt = now, UpdatedAt = now,
        };
    }
}
