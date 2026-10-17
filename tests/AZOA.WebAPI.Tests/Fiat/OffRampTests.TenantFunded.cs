using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using SurrealForge.Client;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// D-8 tenant-funded payouts through <see cref="OffRampService"/> and <see cref="TenantPayoutFunding"/>
/// over an in-memory ledger with the production commit contract. See Services/Fiat/OffRamp/AGENTS.md §tenant-funded.
/// </summary>
public partial class OffRampTests
{
    private static readonly Guid PricingTenant = new("33333333333333333333333333333333");

    private static Harness Funded(long balance)
    {
        var harness = new Harness { PricingTenant = PricingTenant };
        if (balance > 0)
            harness.TenantLedger.SeedAvailable(PricingTenant, balance);
        return harness;
    }

    private static async Task<TenantPayoutFloatView> FloatOf(Harness harness)
        => (await harness.Funding.DescribeAsync(PricingTenant)).Single();

    private static FiatPayout FundedRow(string id, long amount = 500) => new()
    {
        Id = id, Environment = "test", ChainNetwork = "Devnet", SubjectUserId = $"user:{SubjectUserId:N}",
        RequestKey = id, FundingTenantId = $"user:{PricingTenant:N}", ChainType = ChainType, AssetId = AssetId,
        QuantityBaseUnits = "50000", AmountMinorUnits = amount, CurrencyCode = "USD",
        PriceScheduleId = "tenant_price_schedule:tenantschedule", ConnectedAccountId = ConnectedAccountId,
        Status = FiatPayoutStatuses.Requested, RequestedAt = Now, CreatedAt = Now, UpdatedAt = Now,
    };

    /// <summary>Parks the first <paramref name="parties"/> commits until all have read, so they really race.</summary>
    private static void Race(InMemoryTenantPayoutLedger ledger, int parties)
    {
        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ledger.BeforeCommit = async () =>
        {
            if (Interlocked.Increment(ref arrived) >= parties)
                gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };
    }

    [Fact]
    public async Task Two_concurrent_payouts_against_a_balance_covering_one_reserve_exactly_one()
    {
        var harness = Funded(500);
        Race(harness.TenantLedger, 2);

        var outcomes = await Task.WhenAll(
            harness.Funding.ReserveAsync(FundedRow("aaaa0000000000000000000000000001")),
            harness.Funding.ReserveAsync(FundedRow("aaaa0000000000000000000000000002")));

        outcomes.Should().BeEquivalentTo(new[] { TenantFundingReservation.Reserved, TenantFundingReservation.Insufficient });
        harness.TenantLedger.LostCommits.Should().BeGreaterThan(0, "both read the same float; the CAS decided");
        harness.TenantLedger.Debits.Should().ContainSingle();
        var view = await FloatOf(harness);
        view.ReservedMinorUnits.Should().Be(500);
        view.AvailableMinorUnits.Should().Be(0);
        view.Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task An_uncovered_tenant_priced_cash_out_is_refused_with_a_typed_code_before_anything_moves()
    {
        var harness = Funded(400);

        var result = await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);

        result.Code.Should().Be(OffRampService.TenantPayoutBalanceInsufficientCode);
        var row = harness.Payouts.Rows.Single();
        row.Status.Should().Be(FiatPayoutStatuses.Refused);
        harness.SagasStarted.Should().Be(0);
        await harness.Service.SendMoneyAsync(row.Id);
        harness.SentTransfers.Should().BeEmpty();
        harness.TenantLedger.Debits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_payout_reservation_racing_a_top_up_promotion_lands_both_exactly_once()
    {
        var harness = Funded(500);
        var floatRow = await harness.TenantLedger.GetOrCreateFloatAsync(PricingTenant, "USD", "test");
        await harness.TenantLedger.RecordCreditAsync(new TenantPayoutCredit
        {
            Id = "credit-topup", IntentId = "intent-topup", FloatId = floatRow.Id, TenantId = floatRow.TenantId,
            CurrencyCode = "USD", Environment = "test", PaymentIntentId = "pi_topup", GrossMinorUnits = 1000,
            NetMinorUnits = 970, State = TenantPayoutCreditStates.Pending, AvailableAt = Now.AddMinutes(1),
            CreatedAt = Now, UpdatedAt = Now,
        });
        // M5: only the reservation's commit is gated. Its own promotion pass found nothing matured; while it holds
        // the float it read, the credit matures and the standalone promotion commits first.
        var promoted = -1;
        harness.TenantLedger.BeforeCommitOf = async transition =>
        {
            if (transition.Entry is not TenantPayoutEntryChange.CreateDebit || promoted >= 0)
                return;
            harness.Clock.Now = Now.AddMinutes(2);
            promoted = await harness.Funding.PromoteMaturedAsync();
        };

        var reserved = await harness.Funding.ReserveAsync(FundedRow("bbbb0000000000000000000000000001"));

        reserved.Should().Be(TenantFundingReservation.Reserved);
        promoted.Should().Be(1);
        harness.TenantLedger.LostCommits.Should().Be(1, "the reservation's CAS lost to the promotion and re-checked");
        harness.TenantLedger.Debits.Should().ContainSingle();
        var view = await FloatOf(harness);
        view.CreditedMinorUnits.Should().Be(1470, "the top-up is promoted once, net of fees");
        view.ReservedMinorUnits.Should().Be(500);
        view.Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task A_paid_tenant_payout_finalizes_once_and_a_later_reversal_returns_exactly_what_Stripe_reversed()
    {
        var harness = Funded(1000);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;

        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Paid);
        harness.Reattributions.Should().ContainSingle().Which.To.Should().Be(OffRampService.TenantPartitionFor(PricingTenant));
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Finalized);
        (await FloatOf(harness)).FinalizedMinorUnits.Should().Be(500);

        harness.ReversedMinorUnits = 200;
        harness.TransferReversedMinorUnits = 200;
        var handler = harness.BuildReversalHandler();
        await handler.ApplyAsync(harness.ReversalContext(payoutId));
        await handler.ApplyAsync(harness.ReversalContext(payoutId));

        var debit = harness.TenantLedger.Debits.Single();
        debit.ReversedMinorUnits.Should().Be(200);
        var view = await FloatOf(harness);
        view.FinalizedMinorUnits.Should().Be(300);
        view.AvailableMinorUnits.Should().Be(700);
        view.Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task A_crash_after_the_transfer_and_before_its_claim_completed_replays_to_exactly_one_transfer()
    {
        // M5: a real crash — Stripe created the transfer, the pay claim never completed, the row never recorded it.
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        harness.CrashAfterTransferCreated = true;
        var crash = async () => await harness.Service.SendMoneyAsync(payoutId);
        await crash.Should().ThrowAsync<InvalidOperationException>();
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.UnitsCollected);

        // Inside the stale-claim window the resumed saga does nothing; past it, it replays the same Stripe key.
        await harness.Service.SendMoneyAsync(payoutId);
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.UnitsCollected);
        harness.Clock.Now = DateTimeOffset.UtcNow + OffRampService.StalePayClaimAge + TimeSpan.FromMinutes(1);
        await harness.Service.SendMoneyAsync(payoutId);

        harness.CreatedTransfers.Should().ContainSingle("the replay reuses the stable Stripe key");
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Paid);
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Finalized);
        (await FloatOf(harness)).Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task Abandoning_a_reserved_cash_out_before_any_send_releases_the_tenant_debit_once()
    {
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);

        await harness.Service.AbandonAsync(payoutId, "operator cancelled");
        await harness.Service.AbandonAsync(payoutId, "operator cancelled again");

        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Returned);
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Released);
        var view = await FloatOf(harness);
        view.ReservedMinorUnits.Should().Be(0);
        view.AvailableMinorUnits.Should().Be(500);
    }

    [Fact]
    public async Task An_idempotency_conflict_at_send_parks_the_cash_out_and_keeps_the_debit_reserved()
    {
        var harness = Funded(500);
        harness.TransferConflict = true;
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);

        await harness.Service.SendMoneyAsync(payoutId);
        var row = harness.Payouts.Rows.Single();
        await harness.Funding.SyncAsync(row);

        row.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Reserved, "the money may have left");
        (await FloatOf(harness)).ReservedMinorUnits.Should().Be(500);
    }

    [Fact]
    public async Task A_partial_restitution_returns_only_the_recovered_amount_and_a_later_reversal_adds_nothing()
    {
        var harness = Funded(500);
        harness.BankPayoutStatus = "failed";
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        harness.TransferReversedMinorUnits = 300;
        var row = harness.Payouts.Rows.Single();
        (await harness.Funding.SyncAsync(row)).Should().Be(TenantFundingSync.Applied);
        (await harness.Funding.SyncAsync(row)).Should().Be(TenantFundingSync.Unchanged, "the cumulative figure did not move");

        var debit = harness.TenantLedger.Debits.Single();
        debit.State.Should().Be(TenantPayoutDebitStates.Reserved);
        debit.ReversedMinorUnits.Should().Be(300);
        var view = await FloatOf(harness);
        view.ReservedMinorUnits.Should().Be(200);
        view.AvailableMinorUnits.Should().Be(300);
        view.Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task A_credit_promotion_racing_its_reversal_counts_the_net_exactly_once()
    {
        var harness = Funded(0);
        var floatRow = await harness.TenantLedger.GetOrCreateFloatAsync(PricingTenant, "USD", "test");
        await harness.TenantLedger.RecordCreditAsync(new TenantPayoutCredit
        {
            Id = "credit-race", IntentId = "intent-race", FloatId = floatRow.Id, TenantId = floatRow.TenantId,
            CurrencyCode = "USD", Environment = "test", PaymentIntentId = "pi_race", GrossMinorUnits = 1000,
            NetMinorUnits = 1000, State = TenantPayoutCreditStates.Pending, AvailableAt = Now.AddMinutes(-1),
            CreatedAt = Now, UpdatedAt = Now,
        });
        Race(harness.TenantLedger, 2);

        await Task.WhenAll(harness.Funding.PromoteMaturedAsync(), harness.Funding.ApplyCreditReversalAsync("credit-race", 400));

        var credit = harness.TenantLedger.Credits.Single();
        credit.State.Should().Be(TenantPayoutCreditStates.Available);
        credit.ReversedMinorUnits.Should().Be(400);
        var view = await FloatOf(harness);
        view.CreditedMinorUnits.Should().Be(600);
        view.Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task The_sweep_closes_a_cash_out_that_crashed_after_reserving_and_before_its_saga_started()
    {
        var harness = Funded(500);
        var row = FundedRow(FiatPayout.RecordIdFor(SubjectUserId, "crashed"));
        harness.Payouts.Seed(row);
        (await harness.Funding.ReserveAsync(row)).Should().Be(TenantFundingReservation.Reserved);

        harness.Clock.Now = Now + TenantPayoutSweep.AbandonRequestedAfter + TimeSpan.FromMinutes(1);
        await harness.BuildSweep().RunOnceAsync(CancellationToken.None);
        await harness.Service.CollectUnitsAsync(row.Id);

        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Refused);
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Released);
        harness.Transfers.Should().BeEmpty("the fenced collect leg can never move units after the sweep");
        (await FloatOf(harness)).AvailableMinorUnits.Should().Be(500);
    }

    [Fact]
    public async Task A_tenant_priced_row_with_no_debit_parks_at_send()
    {
        var harness = Funded(500);
        var row = FundedRow(FiatPayout.RecordIdFor(SubjectUserId, "legacy"));
        row.Status = FiatPayoutStatuses.UnitsCollected;
        harness.Payouts.Seed(row);

        await harness.Service.SendMoneyAsync(row.Id);

        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        harness.SentTransfers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_driver_priced_by_the_node_fallback_is_node_funded_at_the_node_price()
    {
        var harness = new Harness { NodeFallback = true };
        var driver = Guid.NewGuid();

        await harness.RequestAsync(amountMinorUnits: 500, tenantId: driver);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        var row = harness.Payouts.Rows.Single();
        row.FundingTenantId.Should().BeNull("the node priced it, so the node funds it");
        SurrealLink.FromLink(row.PriceScheduleId).Should().Be("nodeschedule");
        row.Status.Should().Be(FiatPayoutStatuses.Paid);
        harness.TenantLedger.Debits.Should().BeEmpty();
        harness.Reattributions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_transfer_reversed_while_bank_pending_finalizes_the_debit_and_returns_the_float()
    {
        // M4: the failed-bank path ends Reversed, which must be terminal for the debit too.
        var harness = Funded(500);
        harness.BankPayoutStatus = "pending";
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.BankPending);

        harness.ReversedMinorUnits = 500;
        harness.TransferReversedMinorUnits = 500;
        await harness.BuildReversalHandler().ApplyAsync(harness.ReversalContext(payoutId));

        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Reversed);
        var debit = harness.TenantLedger.Debits.Single();
        debit.State.Should().Be(TenantPayoutDebitStates.Finalized);
        debit.ReversedMinorUnits.Should().Be(500);
        var view = await FloatOf(harness);
        view.ReservedMinorUnits.Should().Be(0);
        view.AvailableMinorUnits.Should().Be(500);
        view.Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task One_tenants_float_never_funds_or_drains_another_tenants_cash_out()
    {
        var harness = Funded(0);
        var other = Guid.NewGuid();
        harness.TenantLedger.SeedAvailable(other, 5000);

        var result = await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var row = FundedRow("cccc0000000000000000000000000001");
        var otherFloat = (await harness.Funding.DescribeAsync(other)).Single();

        result.Code.Should().Be(OffRampService.TenantPayoutBalanceInsufficientCode);
        (await harness.Funding.HoldsReservedDebitAsync(row, other)).Should().BeFalse();
        otherFloat.AvailableMinorUnits.Should().Be(5000);
        otherFloat.ReservedMinorUnits.Should().Be(0);
        harness.TenantLedger.Debits.Should().BeEmpty();
    }

    [Fact]
    public async Task The_sweep_reaches_a_crashed_cash_out_behind_more_than_a_page_of_long_lived_debits()
    {
        // M4: 150 bank-pending debits older than the crash; the old oldest-100 read never reached it.
        var harness = Funded(151 * 500);
        for (var i = 0; i < 150; i++)
        {
            var parked = FundedRow($"dddd{i:D28}");
            parked.Status = FiatPayoutStatuses.BankPending;
            harness.Payouts.Seed(parked);
            harness.Clock.Now = Now.AddSeconds(i);
            (await harness.Funding.ReserveAsync(parked)).Should().Be(TenantFundingReservation.Reserved);
        }
        var crashed = FundedRow(FiatPayout.RecordIdFor(SubjectUserId, "crashed-late"));
        harness.Payouts.Seed(crashed);
        harness.Clock.Now = Now.AddMinutes(10);
        (await harness.Funding.ReserveAsync(crashed)).Should().Be(TenantFundingReservation.Reserved);

        harness.Clock.Now = Now + TenantPayoutSweep.AbandonRequestedAfter + TimeSpan.FromMinutes(20);
        await harness.BuildSweep().RunOnceAsync(CancellationToken.None);

        harness.Payouts.Rows.Single(r => r.Id == crashed.Id).Status.Should().Be(FiatPayoutStatuses.Refused);
        harness.TenantLedger.Debits.Single(d => d.Id == crashed.Id).State.Should().Be(TenantPayoutDebitStates.Released);
    }

    [Fact]
    public async Task A_debit_reserved_past_the_threshold_makes_the_float_report_stranded_not_consistent()
    {
        var harness = Funded(500);
        var row = FundedRow("eeee0000000000000000000000000001");
        row.Status = FiatPayoutStatuses.NeedsOperator;
        harness.Payouts.Seed(row);
        await harness.Funding.ReserveAsync(row);

        (await FloatOf(harness)).Consistent.Should().BeTrue();
        harness.Clock.Now = Now + TenantPayoutFunding.StrandedReservedAfter + TimeSpan.FromHours(1);
        var view = await FloatOf(harness);

        view.Consistent.Should().BeFalse("counters agree, but 500 has been held reserved past the threshold");
        view.StrandedReservedCount.Should().Be(1);
        view.OldestReservedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_collect_replayed_after_a_crash_still_returns_the_units_to_the_funding_tenant()
    {
        // L1: the collect claim completed and the process died before the partition draw and the row write.
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        harness.Idempotency.Seed(OffRampService.MoveIdempotencyKey(payoutId, "collect"), OffRampService.PayoutOperationType,
            AZOA.WebAPI.Models.Idempotency.IdempotencyState.Completed, resultPayload: "MOVETX");

        await harness.Service.CollectUnitsAsync(payoutId);

        harness.Transfers.Should().BeEmpty("the replay moves nothing");
        harness.Reattributions.Should().ContainSingle().Which.To.Should().Be(OffRampService.TenantPartitionFor(PricingTenant));
        var row = harness.Payouts.Rows.Single();
        row.Status.Should().Be(FiatPayoutStatuses.UnitsCollected);
        row.CollectPartition.Should().Be(OffRampService.TenantPartitionFor(PricingTenant));
    }

    [Fact]
    public async Task Units_with_no_lot_cannot_leave_node_funded_above_the_cheapest_cross_scope_sale()
    {
        // H2: U bought cheap at a tenant's price, moved the units on chain to this account, which has no lot.
        var harness = new Harness { Holding = "50000", NodeRate = 50 };
        var sold = Lot("lot-someone-else", "50000", windowEnds: Now.AddDays(-1));
        sold.SubjectUserId = Guid.NewGuid().ToString("N");
        sold.PricingScopeKey = PricingTenant.ToString("N");
        sold.UnitsPerMinorUnit = "100";
        harness.Lots.Seed(sold);

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(OffRampService.SoldPriceCapRefusal);
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Refused);
    }

    [Fact]
    public async Task A_legacy_lot_of_unknown_scope_is_capped_at_the_cross_scope_floor_not_skipped()
    {
        var harness = new Harness { Holding = "50000", NodeRate = 50 };
        var legacy = Lot("lot-legacy", "25000", windowEnds: Now.AddDays(-1));
        legacy.PricingScopeKey = null;
        legacy.UnitsPerMinorUnit = "100";
        harness.Lots.Seed(legacy);

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.Message.Should().Be(OffRampService.SoldPriceCapRefusal);
    }

    [Fact]
    public async Task An_unreadable_sold_price_record_refuses_lot_less_units_rather_than_guessing()
    {
        var harness = new Harness { Holding = "50000" };
        harness.Lots.FailSoldRead = true;

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.Message.Should().Be(OffRampService.SoldPriceUnknownRefusal);
    }

    [Fact]
    public async Task Lot_less_units_at_or_below_the_cross_scope_floor_cash_out_node_funded()
    {
        var harness = new Harness { Holding = "50000", NodeRate = 100 };
        var sold = Lot("lot-someone-else", "50000", windowEnds: Now.AddDays(-1));
        sold.SubjectUserId = Guid.NewGuid().ToString("N");
        sold.PricingScopeKey = PricingTenant.ToString("N");
        sold.UnitsPerMinorUnit = "100";
        harness.Lots.Seed(sold);

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeFalse(result.Message);
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(100, false)]
    public async Task Units_bought_at_a_tenant_price_never_leave_node_funded_above_that_price(int nodeRate, bool refused)
    {
        var harness = new Harness { Holding = "50000", NodeRate = nodeRate };
        var lot = Lot("lot-tenant", "50000", windowEnds: Now.AddDays(-1));
        lot.PricingScopeKey = PricingTenant.ToString("N");
        lot.UnitsPerMinorUnit = "100";
        harness.Lots.Seed(lot);

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().Be(refused);
        harness.Payouts.Rows.Single().Status.Should().Be(refused ? FiatPayoutStatuses.Refused : FiatPayoutStatuses.Requested);
    }
}
