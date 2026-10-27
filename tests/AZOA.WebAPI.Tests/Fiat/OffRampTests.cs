// SPDX-License-Identifier: UNLICENSED
// W8.1 -- the fiat off-ramp: a person turns their balance into money in their own bank account.
//
// Every test here pins one control that a prose claim would otherwise stand in for:
//   * units bought with a card that can still be charged back CANNOT leave the platform, and
//     units earned for work -- which have no on-ramp lot at all -- can (Q-X2);
//   * the destination bank account is resolved by the NODE from the person's own onboarding
//     record, never from anything a caller sends;
//   * the units are taken BEFORE the money is sent, and the state in between is durable, named
//     and compensated;
//   * a reversed payout is recorded as a fact the node OWES, and never runs the clawback ladder
//     against the person whose bank refused the platform's own payment.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public partial class OffRampTests
{
    private const string ChainType = "Algorand";
    private const string AssetId = "1001";
    private const string UnitName = "ARDA";
    private const string HolderAddress = "HOLDERADDR";
    private const string ReserveAddress = "RESERVEADDR";
    private const string ConnectedAccountId = "acct_test_1";

    /// <summary>The settled base: 1 whole unit = $0.01 at 2 decimals, so one cent buys 100 base units.</summary>
    private const decimal SettledBaseRate = 100m;

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SubjectUserId = new("22222222222222222222222222222222");

    // ── The source-aged rule, as arithmetic ──

    [Fact]
    public void Units_inside_a_reversal_window_are_locked_and_matured_ones_are_not()
    {
        var open = Lot("lot-open", "50000", windowEnds: Now.AddDays(30));
        var matured = Lot("lot-matured", "70000", windowEnds: Now.AddDays(-1));

        OffRampAging.LockedBaseUnits(new[] { open, matured }, Now)
            .Should().Be(new BigInteger(50_000));
    }

    [Fact]
    public void A_lot_that_records_no_reversal_window_never_matures()
    {
        // The window is written from the RAIL's declared settlement profile. A missing one means
        // the node could not establish when the money stops being reversible, and unknown has to
        // lock: treating it as mature is a fail-open on the one question this rule answers.
        var unknown = Lot("lot-unknown", "40000", windowEnds: null);

        OffRampAging.IsMature(unknown, Now).Should().BeFalse();
        OffRampAging.LockedBaseUnits(new[] { unknown }, Now).Should().Be(new BigInteger(40_000));
        OffRampAging.NextUnlockAt(new[] { unknown }, Now).Should().BeNull();
    }

    [Fact]
    public void Locked_units_exclude_what_a_chargeback_or_an_earlier_cash_out_already_took()
    {
        var partly = Lot("lot-partly", "50000", windowEnds: Now.AddDays(30), consumed: "20000");

        OffRampAging.LockedBaseUnits(new[] { partly }, Now).Should().Be(new BigInteger(30_000));
    }

    [Fact]
    public void Consumption_draws_matured_lots_oldest_first()
    {
        var older = Lot("lot-older", "30000", windowEnds: Now.AddDays(-10));
        var newer = Lot("lot-newer", "30000", windowEnds: Now.AddDays(-2));

        var plan = OffRampAging.PlanConsumption(new[] { older, newer }, new BigInteger(40_000), Now);

        plan.Draws.Select(draw => draw.LotId).Should().ContainInOrder("lot-older", "lot-newer");
        plan.Draws[0].BaseUnits.Should().Be(new BigInteger(30_000));
        plan.Draws[1].BaseUnits.Should().Be(new BigInteger(10_000));
        plan.UnagedBaseUnits.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public void Units_beyond_every_matured_lot_are_unaged_and_consume_no_lot()
    {
        // Q-X2: units received for work or from selling stake have no on-ramp lot, were never
        // bought with reversible money, and therefore have nothing to age.
        var matured = Lot("lot-matured", "10000", windowEnds: Now.AddDays(-1));

        var plan = OffRampAging.PlanConsumption(new[] { matured }, new BigInteger(25_000), Now);

        plan.AgedBaseUnits.Should().Be(new BigInteger(10_000));
        plan.UnagedBaseUnits.Should().Be(new BigInteger(15_000));
        plan.Draws.Should().ContainSingle();
    }

    [Fact]
    public void An_immature_lot_is_skipped_rather_than_partly_drawn()
    {
        var open = Lot("lot-open", "50000", windowEnds: Now.AddDays(30));

        var plan = OffRampAging.PlanConsumption(new[] { open }, new BigInteger(10_000), Now);

        plan.Draws.Should().BeEmpty();
        plan.AgedBaseUnits.Should().Be(BigInteger.Zero);
        plan.UnagedBaseUnits.Should().Be(new BigInteger(10_000));
    }

    [Fact]
    public void Cashable_is_the_holding_less_what_is_locked_and_already_promised()
    {
        var availability = new CashOutAvailability(
            HoldingBaseUnits: new BigInteger(100_000),
            LockedBaseUnits: new BigInteger(30_000),
            CommittedBaseUnits: new BigInteger(20_000),
            NextUnlockAt: null);

        availability.CashableBaseUnits.Should().Be(new BigInteger(50_000));
    }

    [Fact]
    public void Cashable_never_goes_negative()
    {
        var availability = new CashOutAvailability(
            new BigInteger(10_000), new BigInteger(30_000), BigInteger.Zero, null);

        availability.CashableBaseUnits.Should().Be(BigInteger.Zero);
    }

    [Fact]
    public void The_quote_rounds_units_up_and_money_down_so_neither_leaks_out_of_the_reserve()
    {
        // Half a cent's worth of units at the settled rate: a person asking for 1 cent gives up
        // the whole 100 base units, and 150 base units are only worth 1 cent back.
        OffRampAging.TryQuoteUnits(1, SettledBaseRate, out var units, out _).Should().BeTrue();
        units.Should().Be(new BigInteger(100));

        OffRampAging.QuoteMinorUnits(new BigInteger(150), SettledBaseRate).Should().Be(1L);
    }

    [Fact]
    public void The_quote_handles_a_fractional_rate_exactly()
    {
        // A rate of 0.5 base units per minor unit must not be rounded to 1 before it divides.
        OffRampAging.QuoteMinorUnits(new BigInteger(10), 0.5m).Should().Be(20L);
        OffRampAging.TryQuoteUnits(20, 0.5m, out var units, out _).Should().BeTrue();
        units.Should().Be(new BigInteger(10));
    }

    [Fact]
    public void A_payment_too_small_to_be_worth_a_unit_is_refused_rather_than_rounded_to_zero()
    {
        OffRampAging.TryQuoteUnits(0, SettledBaseRate, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    // ── The request: what may leave, and what may not ──

    [Fact]
    public async Task A_cash_out_within_the_available_balance_is_claimed_and_handed_to_the_saga()
    {
        var harness = new Harness();

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeFalse();
        var row = harness.Payouts.Rows.Single();
        row.Status.Should().Be(FiatPayoutStatuses.Requested);
        row.AmountMinorUnits.Should().Be(500);
        row.QuantityBaseUnits.Should().Be("50000");
        row.ConnectedAccountId.Should().Be(ConnectedAccountId);
        harness.SagasStarted.Should().Be(1);
    }

    [Fact]
    public async Task Units_still_inside_a_reversal_window_cannot_be_cashed_out()
    {
        // The whole holding was bought with a card that can still be charged back.
        var harness = new Harness { Holding = "50000" };
        harness.Lots.Seed(Lot("lot-open", "50000", windowEnds: Now.AddDays(30)));

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("on hold");
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Refused);
        harness.SagasStarted.Should().Be(0);
    }

    [Fact]
    public async Task The_same_units_become_cashable_once_their_window_has_closed()
    {
        var harness = new Harness { Holding = "50000" };
        harness.Lots.Seed(Lot("lot-matured", "50000", windowEnds: Now.AddDays(-1)));

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeFalse();
        // The lot was drawn down FIFO, and the receipt says which lot paid for it.
        harness.Lots.Rows.Single().ConsumedBaseUnits.Should().Be("50000");
        harness.Payouts.Rows.Single().ReadConsumedLots().Should().ContainKey("lot-matured");
        harness.Payouts.Rows.Single().AgedQuantityBaseUnits.Should().Be("50000");
    }

    [Fact]
    public async Task Units_earned_for_work_have_no_lot_and_are_cashable_immediately()
    {
        // Nothing was ever bought with a card: the person holds 50000 base units and there is no
        // on_ramp_lot row anywhere. Q-X2 says these are unaged.
        var harness = new Harness { Holding = "50000" };

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeFalse();
        harness.Payouts.Rows.Single().AgedQuantityBaseUnits.Should().Be("0");
        harness.Payouts.Rows.Single().ReadConsumedLots().Should().BeEmpty();
    }

    [Fact]
    public async Task More_than_the_balance_is_refused_and_leaves_a_durable_row_saying_why()
    {
        var harness = new Harness { Holding = "10000" };

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeTrue();
        var row = harness.Payouts.Rows.Single();
        row.Status.Should().Be(FiatPayoutStatuses.Refused);
        row.StatusReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_cash_out_already_in_flight_is_subtracted_from_what_may_be_cashed_out_again()
    {
        var harness = new Harness { Holding = "50000" };
        harness.Payouts.Seed(new FiatPayout
        {
            Id = "already-in-flight", Environment = "test", ChainNetwork = "Devnet",
            SubjectUserId = SubjectUserId.ToString("N"),
            RequestKey = "first",
            ChainType = ChainType,
            AssetId = AssetId,
            QuantityBaseUnits = "40000",
            AmountMinorUnits = 400,
            CurrencyCode = "USD",
            Status = FiatPayoutStatuses.UnitsCollected,
            RequestedAt = Now.AddMinutes(-5),
        });

        var result = await harness.RequestAsync(amountMinorUnits: 500, requestKey: "second");

        result.IsError.Should().BeTrue();
        harness.Payouts.Rows.Single(row => row.RequestKey == "second")
            .Status.Should().Be(FiatPayoutStatuses.Refused);
    }

    [Fact]
    public async Task The_same_request_key_resumes_one_cash_out_instead_of_starting_a_second()
    {
        var harness = new Harness();

        var first = await harness.RequestAsync(amountMinorUnits: 500);
        var second = await harness.RequestAsync(amountMinorUnits: 500);

        first.IsError.Should().BeFalse();
        second.IsError.Should().BeFalse();
        second.Result!.Id.Should().Be(first.Result!.Id);
        harness.Payouts.Rows.Should().ContainSingle();
        harness.SagasStarted.Should().Be(1);
    }

    [Fact]
    public async Task Another_driver_reusing_a_request_key_gets_a_conflict_and_never_sees_the_cash_out()
    {
        // F-H8: the replay is honoured only for the tenant (or the person) that drove the cash-out.
        var harness = new Harness();
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();

        var original = await harness.RequestAsync(amountMinorUnits: 500, tenantId: tenantOne);
        var otherTenant = await harness.RequestAsync(amountMinorUnits: 500, tenantId: tenantTwo);
        var firstParty = await harness.RequestAsync(amountMinorUnits: 500);
        var otherHistory = await harness.Service.ListForUserAsync(SubjectUserId, 25, tenantTwo);
        var ownHistory = await harness.Service.ListForUserAsync(SubjectUserId, 25, tenantOne);

        original.IsError.Should().BeFalse();
        otherTenant.Code.Should().Be(FiatConsumerErrors.RequestKeyInUseCode);
        firstParty.Code.Should().Be(FiatConsumerErrors.RequestKeyInUseCode);
        otherHistory.Result.Should().BeEmpty();
        ownHistory.Result.Should().ContainSingle().Which.Id.Should().Be(original.Result!.Id);
        harness.SagasStarted.Should().Be(1);
    }

    [Fact]
    public async Task An_account_the_value_gate_refuses_cannot_cash_out()
    {
        // The registered gate is wrapped by RiskHoldValueGate, so this one refusal is BOTH the
        // D46 identity check and the pause an unresolved chargeback puts on an account.
        var harness = new Harness { ValueGateAllows = false };

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeTrue();
        harness.Payouts.Rows.Should().BeEmpty();
        harness.SagasStarted.Should().Be(0);
    }

    [Fact]
    public async Task An_account_that_cannot_receive_money_cannot_cash_out()
    {
        var harness = new Harness { AccountReady = false };

        var result = await harness.RequestAsync(amountMinorUnits: 500);

        result.IsError.Should().BeTrue();
        harness.Payouts.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_amount_outside_the_configured_bounds_is_refused_before_anything_is_claimed()
    {
        var harness = new Harness();

        var result = await harness.RequestAsync(amountMinorUnits: 1);

        result.IsError.Should().BeTrue();
        harness.Payouts.Rows.Should().BeEmpty();
    }

    // ── Step 1: the units come back, signed by the person ──

    [Fact]
    public async Task Collecting_moves_the_units_from_the_person_to_the_reserve_under_their_own_key()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;

        var outcome = await harness.Service.CollectUnitsAsync(payoutId);

        outcome.IsError.Should().BeFalse();
        outcome.Result!.Definite.Should().BeTrue();

        var move = harness.Transfers.Single();
        move.From.Should().Be(HolderAddress);
        move.To.Should().Be(ReserveAddress);
        move.BaseUnits.Should().Be(new BigInteger(50_000));
        move.Signing.IsPlatform.Should().BeFalse();
        move.Signing.UserId.Should().Be(SubjectUserId);

        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.UnitsCollected);
    }

    [Fact]
    public async Task Collecting_returns_the_units_to_the_partition_they_were_issued_from()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);

        await harness.Service.CollectUnitsAsync(harness.Payouts.Rows.Single().Id);

        var draw = harness.Draws.Single();
        draw.Kind.Should().Be(ReserveDrawKind.Return);
        draw.Partition.Should().Be("on-ramp");
        draw.Amount.Should().Be(new BigInteger(50_000));
    }

    [Fact]
    public async Task A_collection_whose_outcome_cannot_be_established_parks_instead_of_retrying()
    {
        // Submitted, outcome unknown. Retrying blind would take the units twice, so this is where
        // a cash-out stops and asks for a person.
        var harness = new Harness { TransferConfirms = false };
        await harness.RequestAsync(amountMinorUnits: 500);

        var outcome = await harness.Service.CollectUnitsAsync(harness.Payouts.Rows.Single().Id);

        outcome.Result!.Row.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        outcome.Result.Row.StatusReason.Should().Contain("could not be established");
    }

    [Fact]
    public async Task A_collection_that_merely_failed_is_not_re_sent_under_the_same_claim()
    {
        // The provider proves every precondition before signing, so this failure broadcast
        // nothing -- but the claim is spent, and re-sending under a spent claim is exactly the
        // double-move the ledger exists to prevent. The cash-out stays retryable and is
        // compensated by the saga rather than quietly sending a second time.
        var harness = new Harness { TransferFails = true };
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;

        var first = await harness.Service.CollectUnitsAsync(payoutId);
        var second = await harness.Service.CollectUnitsAsync(payoutId);

        first.Result!.Definite.Should().BeFalse();
        second.Result!.Definite.Should().BeFalse();
        harness.Transfers.Should().ContainSingle();
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Requested);
    }

    [Fact]
    public async Task A_cash_out_abandoned_after_an_unconfirmed_collection_stays_with_a_person()
    {
        var harness = new Harness { TransferConfirms = false };
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;

        await harness.Service.CollectUnitsAsync(payoutId);
        var outcome = await harness.Service.AbandonAsync(payoutId, "saga gave up");

        // NeedsPerson short-circuits the unwind: nothing is handed back over an outcome nobody
        // has confirmed, because giving units back that never left would be giving them away.
        outcome.Result!.Row.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        harness.Transfers.Should().ContainSingle();
    }

    // ── Step 2: the money goes out ──

    [Fact]
    public async Task Money_is_never_sent_before_the_units_have_been_collected()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);

        var outcome = await harness.Service.SendMoneyAsync(harness.Payouts.Rows.Single().Id);

        outcome.Result!.Definite.Should().BeFalse();
        harness.SentTransfers.Should().BeEmpty();
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Requested);
    }

    [Fact]
    public async Task The_destination_is_the_account_the_node_resolved_and_the_amount_is_the_row_s()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;

        await harness.Service.CollectUnitsAsync(payoutId);
        var outcome = await harness.Service.SendMoneyAsync(payoutId);

        outcome.Result!.Definite.Should().BeTrue();
        var sent = harness.SentTransfers.Single();
        sent.ConnectedAccountId.Should().Be(ConnectedAccountId);
        sent.AmountMinorUnits.Should().Be(500);
        // The payout id travels in metadata; it is what brings transfer.reversed back here.
        sent.PayoutId.Should().Be(payoutId);
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Paid);
    }

    [Fact]
    public async Task A_refused_transfer_gives_the_units_back_rather_than_leaving_the_person_with_neither()
    {
        var harness = new Harness { TransferRefused = true };
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;

        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        var row = harness.Payouts.Rows.Single();
        row.Status.Should().Be(FiatPayoutStatuses.Returned);

        // Reserve → person, signed by the platform: giving units back needs nobody's key but ours.
        var giveBack = harness.Transfers.Last();
        giveBack.From.Should().Be(ReserveAddress);
        giveBack.To.Should().Be(HolderAddress);
        giveBack.Signing.IsPlatform.Should().BeTrue();
    }

    [Fact]
    public async Task Abandoning_a_collected_cash_out_hands_the_consumed_lot_units_back()
    {
        var harness = new Harness { Holding = "50000" };
        harness.Lots.Seed(Lot("lot-matured", "50000", windowEnds: Now.AddDays(-1)));
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;

        harness.Lots.Rows.Single().ConsumedBaseUnits.Should().Be("50000");

        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.AbandonAsync(payoutId, "operator cancelled");

        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Returned);
        harness.Lots.Rows.Single().ConsumedBaseUnits.Should().Be("0");
    }

    [Fact]
    public async Task A_cash_out_whose_units_cannot_be_given_back_records_the_debt_and_asks_for_a_person()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);

        harness.TransferFails = true;
        await harness.Service.AbandonAsync(payoutId, "operator cancelled");

        var row = harness.Payouts.Rows.Single();
        row.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        row.UnitsOwedBaseUnits.Should().Be("50000");
    }

    // ── A reversed payout runs the other way ──

    [Fact]
    public async Task RestitutionCrashAfterSubmissionReplaysPersistedTransactionWithoutPreparingAgain()
    {
        var harness = new Harness();
        await harness.RequestAsync(500);
        var id = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(id);
        harness.CrashAfterPreparedSubmission = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Service.AbandonAsync(id, "Refund failed payout"));
        harness.CrashAfterPreparedSubmission = false;
        await harness.Service.AbandonAsync(id, "Resume original return");
        harness.PreparedCount.Should().Be(1);
        harness.Transfers.Where(move => move.From == ReserveAddress).Should().ContainSingle();
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Returned);
    }

    [Fact]
    public async Task RestitutionDrawFailureKeepsReceiptAndRepairsAccountingWithoutSecondTransfer()
    {
        var harness = new Harness();
        await harness.RequestAsync(500);
        var id = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(id);
        harness.FailReturnDraw = true;
        await harness.Service.AbandonAsync(id, "Return collected units");
        harness.Payouts.Rows.Single().Status.Should().NotBe(FiatPayoutStatuses.Returned);
        harness.FailReturnDraw = false;
        await harness.Service.AbandonAsync(id, "Repair accounting");
        harness.Transfers.Where(move => move.From == ReserveAddress).Should().ContainSingle();
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Returned);
    }

    [Fact]
    public async Task A_reversed_payout_is_recorded_as_a_debt_the_node_owes_and_never_flags_the_account()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        var outcome = await harness.BuildReversalHandler().ApplyAsync(harness.ReversalContext(payoutId));

        outcome.Should().Be(FiatEventOutcome.Applied);

        var payout = harness.Payouts.Rows.Single();
        payout.Status.Should().Be(FiatPayoutStatuses.Reversed);
        payout.ReversedMinorUnits.Should().Be(500);
        payout.UnitsOwedBaseUnits.Should().Be("50000");

        var reversal = harness.Reversals.Rows.Single();
        // NeedsOperator, so neither RecoverAsync nor SettleShortfallAsync will ever run the
        // clawback ladder against somebody whose bank refused OUR payment.
        reversal.Status.Should().Be(FiatReversalStatuses.NeedsOperator);
        reversal.AccountFlagged.Should().BeFalse();
        reversal.TargetQuantityBaseUnits.Should().Be("0");
        reversal.ReinstatementOwedBaseUnits.Should().Be("50000");
        reversal.FlowPurpose.Should().Be(PayoutReversalEventHandler.FlowPurpose);
    }

    [Fact]
    public async Task The_reversed_amount_comes_from_the_provider_and_not_from_the_delivery()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        // The provider reports only HALF the payment came back. The delivery says nothing about
        // amounts at all -- FiatEventContext exposes none -- so this is the only figure there is.
        harness.ReversedMinorUnits = 250;

        await harness.BuildReversalHandler().ApplyAsync(harness.ReversalContext(payoutId));

        var payout = harness.Payouts.Rows.Single();
        payout.ReversedMinorUnits.Should().Be(250);
        payout.UnitsOwedBaseUnits.Should().Be("25000");
        harness.Reversals.Rows.Single().IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task A_reversal_the_provider_does_not_show_is_retried_rather_than_written_down()
    {
        var harness = new Harness();
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        harness.ReversedMinorUnits = 0;

        var outcome = await harness.BuildReversalHandler().ApplyAsync(harness.ReversalContext(payoutId));

        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.Reversals.Rows.Should().BeEmpty();
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Paid);
    }

    [Fact]
    public async Task A_reversal_for_a_transfer_this_node_never_sent_is_ignored()
    {
        var harness = new Harness();

        var outcome = await harness.BuildReversalHandler()
            .ApplyAsync(harness.ReversalContext(Guid.NewGuid().ToString("N")));

        outcome.Should().Be(FiatEventOutcome.Ignored);
        harness.Reversals.Rows.Should().BeEmpty();
    }

    // ── fixtures ──

    private static OnRampLot Lot(
        string id, string quantity, DateTimeOffset? windowEnds, string consumed = "0") => new()
    {
        Environment = "test", ChainNetwork = "Devnet",
        Id = id,
        IntentId = id,
        SubjectUserId = SubjectUserId.ToString("N"),
        ChainType = ChainType,
        AssetId = AssetId,
        QuantityBaseUnits = quantity,
        GrossQuantityBaseUnits = quantity,
        ConsumedBaseUnits = consumed,
        CurrencyCode = "USD",
        PricingScopeKey = PriceScheduleScope.Node,
        UnitsPerMinorUnit = "100",
        Status = OnRampLotStatuses.Issued,
        IssuedAt = windowEnds?.AddDays(-120) ?? Now.AddDays(-1),
        ReversalWindowEnds = windowEnds,
        Version = 0,
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
    };

    private sealed record TransferCall(
        string AssetId, string From, string To, BigInteger BaseUnits, SigningContext Signing);

    /// <summary>Builds the off-ramp over fakes, with one knob per behaviour under test.</summary>
    private sealed class Harness
    {
        private OffRampService? _service;

        public string Holding { get; init; } = "100000";

        public bool ValueGateAllows { get; init; } = true;

        public bool AccountReady { get; init; } = true;

        public bool TransferConfirms { get; set; } = true;
        public bool CrashAfterPreparedSubmission { get; set; }
        public bool FailReturnDraw { get; set; }
        public int PreparedCount { get; private set; }

        public bool TransferRefused { get; init; }

        public bool TransferFails { get; set; }

        public long ReversedMinorUnits { get; set; } = 500;

        public FakePayoutLedger Payouts { get; } = new();

        public FakeLotLedger Lots { get; } = new();

        public FakeReversalLedger Reversals { get; } = new();

        public FakeIdempotencyStore Idempotency { get; } = new();

        public List<TransferCall> Transfers { get; } = new();

        public List<ReserveDrawRequest> Draws { get; } = new();

        public List<StripeTransferRequest> SentTransfers { get; } = new();

        public int SagasStarted { get; private set; }

        /// <summary>The clock every collaborator reads; tests may move it forward.</summary>
        public FixedClock Clock { get; } = new(Now);

        /// <summary>D-8: the tenant whose schedule prices requests; null = the node's own schedule.</summary>
        public Guid? PricingTenant { get; set; }

        /// <summary>Units per minor unit of the node schedule (the tenant schedule uses <see cref="SettledBaseRate"/>).</summary>
        public decimal NodeRate { get; set; } = SettledBaseRate;

        /// <summary>When true a tenant-scoped request with no tenant schedule falls back to the node's.</summary>
        public bool NodeFallback { get; set; } = true;

        /// <summary>
        /// Stripe answers the funding transfer with an idempotency conflict (409 → busy): a concurrent request under
        /// the same key created the transfer, so the transfer group lists it.
        /// </summary>
        public bool TransferConflict { get; set; }

        /// <summary>The process dies right after Stripe created the transfer, before the pay claim completes.</summary>
        public bool CrashAfterTransferCreated { get; set; }

        /// <summary>The transfer group also holds a transfer whose metadata names another cash-out (L2).</summary>
        public bool ForeignGroupTransfer { get; set; }

        /// <summary>The collect broadcast was sent and not acknowledged: an outcome-unknown provider error (M2).</summary>
        public bool TransferOutcomeUnknown { get; set; }

        /// <summary>What the chain says about a recorded transaction id.</summary>
        public ChainConfirmation ChainVerdict { get; set; } = ChainConfirmation.Unknown;

        /// <summary>Transfers Stripe actually created, one per idempotency key (Stripe's key contract).</summary>
        public IReadOnlyCollection<StripeTransferRequest> CreatedTransfers => _created.Values;
        private readonly Dictionary<string, StripeTransferRequest> _created = new(StringComparer.Ordinal);

        /// <summary>Stripe's cumulative <c>amount_reversed</c> on the cash-out transfer.</summary>
        public long TransferReversedMinorUnits { get; set; }

        /// <summary>The bank payout status Stripe reports.</summary>
        public string BankPayoutStatus { get; set; } = "paid";

        public InMemoryTenantPayoutLedger TenantLedger => _tenantLedger ??= new InMemoryTenantPayoutLedger(Clock);
        private InMemoryTenantPayoutLedger? _tenantLedger;

        public TenantPayoutFunding Funding => _funding ??= new TenantPayoutFunding(
            TenantLedger, Idempotency, ConnectGateway, BuildFiatOptions(), Clock, NullLogger<TenantPayoutFunding>.Instance);
        private TenantPayoutFunding? _funding;

        public List<(ReserveDrawRequest Draw, string To)> Reattributions { get; } = new();

        private IStripeConnectGateway ConnectGateway => _connect ??= BuildConnect().Object;
        private IStripeConnectGateway? _connect;

        public TenantPayoutSweep BuildSweep() => new(TenantLedger, Funding, Payouts, Service, Idempotency, Clock,
            NullLogger<TenantPayoutSweep>.Instance);

        /// <summary>The KYB gate (D-6); approves by default, as an APPROVED kyb_verification row would.</summary>
        public Mock<AZOA.WebAPI.Services.Kyb.IKybGate> Kyb { get; } = ApprovingKyb();

        private static Mock<AZOA.WebAPI.Services.Kyb.IKybGate> ApprovingKyb()
        {
            var gate = new Mock<AZOA.WebAPI.Services.Kyb.IKybGate>();
            gate.Setup(g => g.RequireVerifiedTenantAsync(It.IsAny<Guid>(),
                    It.IsAny<AZOA.WebAPI.Services.Kyb.KybCapability>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<bool>.Success(true));
            return gate;
        }

        /// <summary>Built on first use: the object initialiser runs AFTER the constructor.</summary>
        public OffRampService Service => _service ??= BuildService();

        public Task<AZOAResult<CashOutView>> RequestAsync(
            long amountMinorUnits, string requestKey = "cash-out-1", Guid? tenantId = null)
            => Service.RequestAsync(
                new CashOutRequest(SubjectUserId, tenantId, requestKey, amountMinorUnits));

        public PayoutReversalEventHandler BuildReversalHandler()
        {
            var connect = new Mock<IStripeConnectGateway>();
            connect
                .Setup(c => c.GetTransferAsync(
                    It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeTransfer>.Success(new StripeTransfer
                {
                    TransferId = "tr_test_1",
                    AmountMinorUnits = 500,
                    AmountReversedMinorUnits = ReversedMinorUnits,
                    Currency = "usd",
                    Destination = ConnectedAccountId,
                    Reversed = ReversedMinorUnits >= 500,
                    Livemode = false,
                }));

            return new PayoutReversalEventHandler(
                BuildFiatOptions(),
                Payouts,
                Reversals,
                connect.Object,
                Funding,
                NullLogger<PayoutReversalEventHandler>.Instance,
                Clock);
        }

        public FiatEventContext ReversalContext(string payoutId) => new(
            new FiatEvent
            {
                Id = "event-row",
                Provider = FiatRailIds.StripeProviderKey,
                ProviderEventId = "evt_transfer_reversed_1",
                EventType = PayoutReversalEventHandler.TransferReversedEventType,
                Environment = RailEnvironmentNames.Test,
                Status = FiatEventStatuses.Received,
                PayloadDigest = "digest",
            },
            PayoutReversalEventHandler.TransferReversedEventType,
            null,
            null,
            null,
            Guid.ParseExact(payoutId, "N"),
            SubjectUserId,
            RailEnvironment.Test);

        private static FiatOptions BuildFiatOptions()
        {
            var fiat = new FiatOptions
            {
                Enabled = true,
                Environment = RailEnvironmentNames.Test,
                Test = { IsEnabled = true, SecretKey = "sk_test_x", WebhookSecret = "whsec_x" },
            };
            fiat.Checkout.Currency = "usd";
            fiat.Checkout.AllowedReturnOrigins.Add("https://app.example");
            fiat.Checkout.AllowedPurposes.Add(FiatCheckoutPurposes.FundsTopUp);
            return fiat;
        }

        private Mock<IStripeConnectGateway> BuildConnect()
        {
            var connect = new Mock<IStripeConnectGateway>();
            connect
                .Setup(c => c.CreatePayoutTransferAsync(
                    It.IsAny<StripeCredentials>(), It.IsAny<StripeTransferRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((StripeCredentials _, StripeTransferRequest request, CancellationToken __) =>
                {
                    SentTransfers.Add(request);
                    if (TransferRefused)
                        return FiatPayoutErrors.RailRefused<StripeTransfer>("corr");
                    _created.TryAdd(request.IdempotencyKey, request);
                    if (TransferConflict)
                        return FiatPayoutErrors.RailBusy<StripeTransfer>("corr");
                    if (CrashAfterTransferCreated)
                    {
                        CrashAfterTransferCreated = false;
                        throw new InvalidOperationException("The process stopped after Stripe created the transfer.");
                    }
                    return AZOAResult<StripeTransfer>.Success(new StripeTransfer
                        {
                            TransferId = "tr_test_1",
                            AmountMinorUnits = request.AmountMinorUnits,
                            Currency = request.Currency,
                            Destination = request.ConnectedAccountId,
                            Livemode = false,
                        });
                });

            connect.Setup(c => c.GetTransferAsync(It.IsAny<StripeCredentials>(), "tr_test_1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeTransfer>.Success(new() { TransferId = "tr_test_1",
                    IntentId = SentTransfers.Last().PayoutId, Destination = ConnectedAccountId,
                    AmountMinorUnits = SentTransfers.Last().AmountMinorUnits, Currency = SentTransfers.Last().Currency }));
            connect.Setup(c => c.ConfigureManualPayoutsAsync(
                    It.IsAny<StripeCredentials>(), ConnectedAccountId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<StripeConnectedAccount>.Success(new StripeConnectedAccount
                { AccountId = ConnectedAccountId, PayoutSchedule = "manual", PayoutsEnabled = true }));
            connect.Setup(c => c.GetConnectedAccountAsync(
                    It.IsAny<StripeCredentials>(), ConnectedAccountId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<StripeConnectedAccount>.Success(new StripeConnectedAccount
                { AccountId = ConnectedAccountId, PayoutSchedule = "manual", PayoutsEnabled = true }));
            connect.Setup(c => c.CreateBankPayoutAsync(
                    It.IsAny<StripeCredentials>(), It.IsAny<StripeTransferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((StripeCredentials _, StripeTransferRequest request, CancellationToken __) =>
                    AZOAResult<StripeBankPayout>.Success(new StripeBankPayout
                    {
                        PayoutId = "po_test_1", IntentId = request.PayoutId, Status = BankPayoutStatus,
                        AmountMinorUnits = request.AmountMinorUnits, Currency = request.Currency,
                    }));

            connect.Setup(c => c.ListTransfersByGroupAsync(It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((StripeCredentials _, string group, CancellationToken _) =>
                    AZOAResult<StripeTransferList>.Success(new StripeTransferList(_created.Values
                        .Where(sent => OffRampService.TransferGroupFor(sent.PayoutId) == group)
                        .Select(sent => new StripeTransfer
                        {
                            TransferId = "tr_test_1", IntentId = sent.PayoutId, AmountMinorUnits = sent.AmountMinorUnits,
                            AmountReversedMinorUnits = TransferReversedMinorUnits, Currency = sent.Currency,
                            Destination = sent.ConnectedAccountId, Livemode = false,
                        })
                        .Concat(ForeignGroupTransfer
                            ? [new StripeTransfer { TransferId = "tr_foreign", IntentId = "someone-else", AmountMinorUnits = 500,
                                Currency = "usd", Destination = ConnectedAccountId }]
                            : Array.Empty<StripeTransfer>())
                        .ToList(), HasMore: false)));
            return connect;
        }

        private OffRampService BuildService()
        {
            var options = new OffRampOptions
            {
                Enabled = true,
                OnboardingReturnUrl = "https://app.example/payouts/done",
                OnboardingRefreshUrl = "https://app.example/payouts/retry",
            };

            var nodeSchedule = new RegisteredPriceSchedule(
                "nodeschedule", PriceScheduleScope.Node, null, ChainType, AssetId,
                "USD", 1, NodeRate, "test", null, PriceScheduleStatuses.Active, Now.AddDays(-1), Now.AddDays(-1));
            RegisteredPriceSchedule? TenantSchedule(string scope) => PricingTenant is { } tenant && scope == tenant.ToString("N")
                ? new RegisteredPriceSchedule("tenantschedule", scope, tenant, ChainType, AssetId,
                    "USD", 1, SettledBaseRate, "test", null, PriceScheduleStatuses.Active, Now.AddDays(-1), Now.AddDays(-1))
                : null;
            var schedules = new Mock<ITenantPriceScheduleStore>();
            schedules
                .Setup(s => s.GetLatestAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string scope, string _, string _, CancellationToken _) => new AZOAResult<RegisteredPriceSchedule?>
                {
                    Result = PriceScheduleScope.IsNode(scope) ? nodeSchedule : TenantSchedule(scope),
                });
            schedules
                .Setup(s => s.GetVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => new AZOAResult<RegisteredPriceSchedule?>
                {
                    Result = SurrealForge.Client.SurrealLink.FromLink(id) switch
                    {
                        "nodeschedule" => nodeSchedule,
                        "tenantschedule" when PricingTenant is { } tenant => TenantSchedule(tenant.ToString("N")),
                        _ => null,
                    },
                });

            var accounts = new Mock<IPayoutAccountService>();
            accounts
                .Setup(a => a.RequireReadyAccountAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AccountReady
                    ? AZOAResult<string>.Success(ConnectedAccountId, "Success")
                    : AZOAResult<string>.FailureWithCode(
                        PayoutAccountService.SetupNeededMessage, AzoaErrorCodes.InvalidRequest));
            accounts
                .Setup(a => a.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<PayoutAccountStatus>.Success(
                    new PayoutAccountStatus(true, true, AccountReady, true, 0, null, null, "ok"), "Success"));

            var connect = Mock.Get(ConnectGateway);

            var platformToken = new Mock<IPlatformTokenGenesis>();
            platformToken
                .Setup(p => p.GetStateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<PlatformTokenState>.Success(new PlatformTokenState(
                    new PlatformTokenDefinition(
                        ChainType, "Devnet", AssetId, "Arda", UnitName, 2,
                        new BigInteger(1_000_000_000), ReserveAddress, Now.AddDays(-1)),
                    new PlatformTokenSupply(
                        new BigInteger(1_000_000_000), BigInteger.Zero, BigInteger.Zero,
                        new BigInteger(1_000_000_000)))));

            var reserve = new Mock<IPlatformReserve>();
            reserve
                .Setup(r => r.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string chain, CancellationToken _) =>
                    AZOAResult<ReserveAccount>.Success(new ReserveAccount(chain, ReserveAddress, true)));
            reserve
                .Setup(r => r.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ReserveDrawRequest request, CancellationToken _) =>
                {
                    Draws.Add(request);
                    if (FailReturnDraw && request.Kind == ReserveDrawKind.Issue) return AZOAResult<ReserveDrawOutcome>.Failure("Accounting unavailable");
                    return AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(
                        request.AssetId, request.Partition, request.Kind, request.Amount,
                        ReservePartitionCounters.Declaring(new BigInteger(1_000_000)), 1, false));
                });
            reserve
                .Setup(r => r.ReattributeReturnAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ReserveDrawRequest request, string to, CancellationToken _) =>
                {
                    Reattributions.Add((request, to));
                    return AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(
                        request.AssetId, to, request.Kind, request.Amount,
                        ReservePartitionCounters.Declaring(request.Amount), 1, false));
                });

            var asa = new Mock<IAlgorandASAModule>();
            asa
                .Setup(a => a.GetAssetHoldingAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<string>.Success(Holding));

            var module = asa.Object;
            var provider = new Mock<IBlockchainProvider>();
            provider
                .Setup(p => p.TryGetModule<IAlgorandASAModule>(out It.Ref<IAlgorandASAModule?>.IsAny))
                .Returns((out IAlgorandASAModule? m) => { m = module; return true; });
            provider
                .Setup(p => p.TransferAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RailAmount>(),
                    It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string assetId, string from, string to, RailAmount amount,
                    SigningContext? signing, CancellationToken _) =>
                {
                    Transfers.Add(new TransferCall(
                        assetId, from, to, amount.BaseUnits, signing ?? SigningContext.Platform));

                    if (TransferFails)
                        return AZOAResult<string>.Failure("The node could not be reached.");
                    if (TransferOutcomeUnknown)
                        return AZOAResult<string>.Failure(
                            "Broadcast failed to transfer (tx AMBIGTX); reconcile against chain truth before re-sending: timeout");

                    return TransferConfirms
                        ? AZOAResult<string>.Success("MOVETX", "confirmed in round 42")
                        : AZOAResult<string>.Success("MOVETX", OperationStatus.PendingConfirmationMarker);
                });

            var preparedModule = new Mock<IValueTransferTransactionModule>();
            var submitted = new HashSet<string>();
            preparedModule.Setup(m => m.PrepareValueTransferAsync(It.IsAny<ValueTransferIntent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ValueTransferIntent intent, CancellationToken _) => {
                    PreparedCount++;
                    return TransferFails ? AZOAResult<PreparedValueTransfer>.Failure("Preparation unavailable")
                        : AZOAResult<PreparedValueTransfer>.Success(new("RETURN_TX", "SIGNED_WIRE", "test-genesis", intent));
                });
            preparedModule.Setup(m => m.SubmitValueTransferAsync(It.IsAny<PreparedValueTransfer>(), It.IsAny<CancellationToken>()))
                .Returns((PreparedValueTransfer prepared, CancellationToken _) => {
                    if (submitted.Add(prepared.TransactionId)) Transfers.Add(new(prepared.Intent.AssetId,
                        prepared.Intent.Sender, prepared.Intent.Recipient, BigInteger.Parse(prepared.Intent.QuantityBaseUnits), prepared.Intent.SigningContext));
                    if (CrashAfterPreparedSubmission) throw new HttpRequestException("Lost submission response");
                    return Task.CompletedTask;
                });
            preparedModule.Setup(m => m.ObserveValueTransferAsync(It.IsAny<PreparedValueTransfer>(), It.IsAny<CancellationToken>()))
                // A transaction never submitted is not observable on chain (Unknown), which is what
                // makes the service submit it; a submitted one is Confirmed or still Pending.
                .ReturnsAsync((PreparedValueTransfer prepared, CancellationToken _) => AZOAResult<ChainConfirmation>.Success(
                    !submitted.Contains(prepared.TransactionId) ? ChainConfirmation.Unknown
                    : TransferConfirms ? ChainConfirmation.Confirmed : ChainConfirmation.Pending));
            provider.Setup(p => p.TryGetModule<IValueTransferTransactionModule>(out It.Ref<IValueTransferTransactionModule?>.IsAny))
                .Returns((out IValueTransferTransactionModule? valueModule) => { valueModule = preparedModule.Object; return true; });

            provider.Setup(p => p.GetTransactionConfirmationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<ChainConfirmation>.Success(ChainVerdict));

            var factory = new Mock<IBlockchainProviderFactory>();
            factory
                .Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(provider.Object);
            factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);

            var wallets = new Mock<IWalletStore>();
            wallets
                .Setup(w => w.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>>
                {
                    Result = new[]
                    {
                        Mock.Of<IWallet>(x => x.Id == Guid.NewGuid() && x.UserId == SubjectUserId
                                              && x.ChainType == ChainType && x.Address == HolderAddress),
                    },
                });

            var kyc = new Mock<IRealValueKycGate>();
            kyc.Setup(g => g.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ValueGateAllows
                    ? AZOAResult<bool>.Success(true)
                    : AZOAResult<bool>.Failure(RiskHoldValueGate.RiskHoldMessage));
            kyc.Setup(g => g.RequireCurrentApprovalAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ValueGateAllows
                    ? AZOAResult<bool>.Success(true)
                    : AZOAResult<bool>.Failure(RiskHoldValueGate.RiskHoldMessage));

            var sagas = new Mock<ISagaCoordinator>();
            sagas
                .Setup(s => s.StartAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OffRampCollectPayload>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => SagasStarted++)
                .Returns(Task.CompletedTask);

            return new OffRampService(
                BuildFiatOptions(),
                options,
                new OnRampOptions { AllowNodeDefaultScheduleFallback = NodeFallback },
                Payouts,
                Lots,
                schedules.Object,
                accounts.Object,
                connect.Object,
                platformToken.Object,
                reserve.Object,
                factory.Object,
                wallets.Object,
                Idempotency,
                kyc.Object,
                sagas.Object,
                Kyb.Object,
                Funding,
                NullLogger<OffRampService>.Instance,
                Clock);
        }
    }

    /// <summary>In-memory payout ledger with the production CAS contract.</summary>
    private sealed class FakePayoutLedger : IFiatPayoutLedger
    {
        private readonly Dictionary<string, FiatPayout> _rows = new(StringComparer.Ordinal);

        public IReadOnlyCollection<FiatPayout> Rows => _rows.Values.Select(Clone).ToList();

        public void Seed(FiatPayout row) => _rows[row.Id] = row;

        public Task<AZOAResult<FiatPayoutClaim>> ClaimAsync(
            FiatPayout candidate, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(candidate.Id, out var existing))
                return Task.FromResult(AZOAResult<FiatPayoutClaim>.Success(
                    new FiatPayoutClaim(Clone(existing), Created: false)));

            _rows[candidate.Id] = Clone(candidate);
            return Task.FromResult(AZOAResult<FiatPayoutClaim>.Success(
                new FiatPayoutClaim(Clone(candidate), Created: true)));
        }

        public Task<AZOAResult<FiatPayout?>> GetAsync(string recordId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<FiatPayout?>
            {
                Result = _rows.TryGetValue(recordId, out var row) ? Clone(row) : null,
            });

        public Task<AZOAResult<FiatPayout?>> GetByTransferAsync(
            string transferId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<FiatPayout?>
            {
                Result = _rows.Values
                    .Where(row => string.Equals(row.ProviderTransferId, transferId, StringComparison.Ordinal))
                    .Select(Clone)
                    .FirstOrDefault(),
            });

        public Task<AZOAResult<FiatPayout>> UpdateIfVersionAsync(
            FiatPayout row, long expectedVersion, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion)
                return Task.FromResult(AZOAResult<FiatPayout>.Failure(
                    $"Cash-out '{row.Id}' changed underneath this update."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<FiatPayout>.Success(Clone(row), "Saved."));
        }

        public Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListForUserAsync(
            Guid userId, int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<FiatPayout>>.Success(
                _rows.Values
                    .Where(row => SurrealForge.Client.SurrealLink.FromLink(row.SubjectUserId) == userId.ToString("N"))
                    .OrderByDescending(row => row.RequestedAt)
                    .Take(limit)
                    .Select(Clone)
                    .ToList()));

        public Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListCommittingForUserAsync(
            Guid userId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<FiatPayout>>.Success(
                _rows.Values
                    .Where(row => SurrealForge.Client.SurrealLink.FromLink(row.SubjectUserId) == userId.ToString("N")
                                  && FiatPayoutStatuses.Committing.Contains(row.Status))
                    .Select(Clone)
                    .ToList()));

        public Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListBoardAsync(
            int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<FiatPayout>>.Success(
                _rows.Values
                    .Where(row => FiatPayoutStatuses.Board.Contains(row.Status))
                    .Select(Clone)
                    .ToList()));

        private static FiatPayout Clone(FiatPayout row) => System.Text.Json.JsonSerializer.Deserialize<FiatPayout>(System.Text.Json.JsonSerializer.Serialize(row))!;
    }

    /// <summary>In-memory on-ramp lot ledger with the production CAS contract.</summary>
    private sealed class FakeLotLedger : IOnRampLotLedger
    {
        private readonly Dictionary<string, OnRampLot> _rows = new(StringComparer.Ordinal);

        public IReadOnlyCollection<OnRampLot> Rows => _rows.Values.Select(Clone).ToList();

        public void Seed(OnRampLot lot) => _rows[lot.Id] = lot;

        public Task<AZOAResult<OnRampLotClaim>> ClaimAsync(
            OnRampLot candidate, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(candidate.Id, out var existing))
                return Task.FromResult(AZOAResult<OnRampLotClaim>.Success(
                    new OnRampLotClaim(Clone(existing), Created: false)));

            _rows[candidate.Id] = Clone(candidate);
            return Task.FromResult(AZOAResult<OnRampLotClaim>.Success(
                new OnRampLotClaim(Clone(candidate), Created: true)));
        }

        public Task<AZOAResult<OnRampLot?>> GetByIntentAsync(
            string intentId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<OnRampLot?>
            {
                Result = _rows.Values
                    .Where(row => string.Equals(row.IntentId, intentId, StringComparison.Ordinal))
                    .Select(Clone)
                    .FirstOrDefault(),
            });

        public Task<AZOAResult<OnRampLot>> UpdateIfVersionAsync(
            OnRampLot row, long expectedVersion, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion)
                return Task.FromResult(AZOAResult<OnRampLot>.Failure(
                    $"On-ramp lot '{row.Id}' changed underneath this update."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<OnRampLot>.Success(Clone(row), "Saved."));
        }

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListIssuedForUserAsync(
            Guid userId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                _rows.Values
                    .Where(row => SurrealForge.Client.SurrealLink.FromLink(row.SubjectUserId) == userId.ToString("N")
                                  && row.Status == OnRampLotStatuses.Issued)
                    .OrderBy(row => row.IssuedAt)
                    .Select(Clone)
                    .ToList()));

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListNeedingOperatorAsync(
            int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                _rows.Values
                    .Where(row => OnRampLotStatuses.NeedsOperator(row.Status))
                    .Select(Clone)
                    .ToList()));

        public bool FailSoldRead { get; set; }

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListSoldSinceAsync(DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult(FailSoldRead
                ? AZOAResult<IReadOnlyList<OnRampLot>>.Failure("lot store unavailable")
                : AZOAResult<IReadOnlyList<OnRampLot>>.Success(_rows.Values
                    .Where(row => row.CreatedAt >= since && row.IssuedAt is not null).Select(Clone).ToList()));

        private static OnRampLot Clone(OnRampLot row) => System.Text.Json.JsonSerializer.Deserialize<OnRampLot>(System.Text.Json.JsonSerializer.Serialize(row))!;
    }

    /// <summary>In-memory reversal ledger; only the members the off-ramp actually reaches.</summary>
    private sealed class FakeReversalLedger : IFiatReversalLedger
    {
        private readonly Dictionary<string, FiatReversal> _rows = new(StringComparer.Ordinal);

        public IReadOnlyCollection<FiatReversal> Rows => _rows.Values.ToList();

        public Task<AZOAResult<FiatReversalClaim>> ClaimAsync(
            FiatReversal candidate, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(candidate.Id, out var existing))
                return Task.FromResult(AZOAResult<FiatReversalClaim>.Success(
                    new FiatReversalClaim(existing, Created: false)));

            _rows[candidate.Id] = candidate;
            return Task.FromResult(AZOAResult<FiatReversalClaim>.Success(
                new FiatReversalClaim(candidate, Created: true)));
        }

        public Task<AZOAResult<FiatReversal?>> GetAsync(string recordId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<FiatReversal?>
            {
                Result = _rows.TryGetValue(recordId, out var row) ? row : null,
            });

        public Task<AZOAResult<FiatReversal>> UpdateIfVersionAsync(
            FiatReversal row, long expectedVersion, CancellationToken ct = default)
        {
            _rows[row.Id] = row;
            return Task.FromResult(AZOAResult<FiatReversal>.Success(row, "Saved."));
        }

        public Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListOpenAsync(
            int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<FiatReversal>>.Success(_rows.Values.ToList()));

        public Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListForUserAsync(
            Guid userId, int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<FiatReversal>>.Success(_rows.Values.ToList()));

        public Task<AZOAResult<bool>> HasActiveFlagAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Success(
                _rows.Values.Any(row => row.AccountFlagged && SurrealForge.Client.SurrealLink.FromLink(row.SubjectUserId) == userId.ToString("N"))));
    }

    /// <summary>A clock that does not move, so a window assertion is exact.</summary>
    private sealed class FixedClock : TimeProvider
    {
        public FixedClock(DateTimeOffset now) => Now = now;

        /// <summary>The instant every reader sees; settable so a test can age durable rows.</summary>
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
