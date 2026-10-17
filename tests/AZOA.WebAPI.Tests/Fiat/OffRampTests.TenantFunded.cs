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
            NetMinorUnits = 970, State = TenantPayoutCreditStates.Pending, AvailableAt = Now.AddMinutes(-1),
            CreatedAt = Now, UpdatedAt = Now,
        });
        Race(harness.TenantLedger, 2);

        await Task.WhenAll(harness.Funding.PromoteMaturedAsync(), harness.Funding.ReserveAsync(FundedRow("bbbb0000000000000000000000000001")));

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
    public async Task A_crash_between_reserve_and_send_resumes_with_exactly_one_transfer()
    {
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);

        // The process stops here; a restarted saga runs the send leg, twice for good measure.
        await harness.Service.SendMoneyAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        harness.SentTransfers.Should().ContainSingle();
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
