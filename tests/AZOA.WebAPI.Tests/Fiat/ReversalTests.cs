// SPDX-License-Identifier: UNLICENSED
// W4.3 -- the reversal pipeline (D10): what the node does when money it has already turned into
// units is taken back.
//
// Every test here pins one control that a prose claim would otherwise stand in for:
//   * how much is taken back comes from the provider's own re-read figures and from nothing a
//     delivery can influence -- the on-ramp's pricing rule, read backwards;
//   * a dispute that has merely been RAISED pauses the account and recovers nothing;
//   * the ladder never takes back more than is owed, never more than the person holds, and never
//     anything it cannot attribute to this payment;
//   * a shortfall becomes a RECORDED platform loss plus an enforced pause, not a silence;
//   * the pause is a control: the D46 value gate refuses a flagged account, and fails closed when
//     it cannot tell.

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
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public class ReversalTests
{
    private const string ChainType = "Algorand";
    private const string AssetId = "1001";
    private const string ChargeId = "ch_test_1";
    private const string PaymentIntentId = "pi_test_1";
    private const string SessionId = "cs_test_1";
    private const string DisputeId = "du_test_1";
    private const string HolderAddress = "PAYERALGOADDRESS";
    private const string ReserveAddress = "RESERVEALGOADDRESS";

    /// <summary>$10.00 bought 100_000 base units at the settled base of 1 unit = $0.01.</summary>
    private const long PaymentMinorUnits = 1000L;
    private const string IssuedBaseUnits = "100000";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid IntentId = new("11111111111111111111111111111111");
    private static readonly Guid SubjectUserId = new("22222222222222222222222222222222");

    // ── The money-printer, read backwards: the delivery cannot say how much comes back ──

    [Fact]
    public async Task The_reversed_amount_is_the_re_read_provider_figure()
    {
        var harness = new Harness();
        harness.Charge = Charge(amountRefunded: 400L);

        var outcome = await harness.ApplyRefundAsync();

        outcome.Should().Be(FiatEventOutcome.Applied);
        var row = harness.Reversals.Single();

        // FiatEventContext exposes no amount at all, so only the server-side re-read can move
        // this figure -- and the unit target moves with it.
        row.AmountMinorUnits.Should().Be(400L);
        row.SourceAmountMinorUnits.Should().Be(PaymentMinorUnits);
        row.IsPartial.Should().BeTrue();
        row.TargetQuantityBaseUnits.Should().Be("40000");
    }

    [Fact]
    public async Task A_second_partial_refund_accumulates_onto_the_same_reversal()
    {
        var harness = new Harness();
        harness.Charge = Charge(amountRefunded: 300L);
        await harness.ApplyRefundAsync();

        // Stripe reports amount_refunded CUMULATIVELY, which is what makes two partial refunds
        // accumulate instead of racing each other into two rows.
        harness.Charge = Charge(amountRefunded: 900L);
        await harness.ApplyRefundAsync();

        harness.Reversals.Should().HaveCount(1);
        var row = harness.Reversals.Single();
        row.AmountMinorUnits.Should().Be(900L);
        row.TargetQuantityBaseUnits.Should().Be("90000");
    }

    [Fact]
    public async Task A_refund_the_provider_has_not_actually_made_records_nothing()
    {
        var harness = new Harness();
        harness.Charge = Charge(amountRefunded: 0L);

        var outcome = await harness.ApplyRefundAsync();

        outcome.Should().Be(FiatEventOutcome.Ignored);
        harness.Reversals.Should().BeEmpty();
    }

    [Fact]
    public void A_partial_reversal_rounds_up_and_never_past_what_was_issued()
    {
        var issued = new BigInteger(100);

        // 1/3 of 100 units is 33.33: rounding down would leave the platform a fraction short on
        // every partial reversal.
        ReversalMath.ProportionalTarget(issued, 1, 3).Should().Be(new BigInteger(34));

        // ...and the cap is what stops rounding up from taking back more than was bought.
        ReversalMath.ProportionalTarget(issued, 3, 3).Should().Be(issued);
        ReversalMath.ProportionalTarget(issued, 9, 3).Should().Be(issued);
        ReversalMath.ProportionalTarget(issued, 0, 3).Should().Be(BigInteger.Zero);
    }

    // ── Disputes: a question is not a debit ──

    [Fact]
    public async Task A_dispute_that_is_only_opened_pauses_the_account_and_takes_nothing_back()
    {
        var harness = new Harness();
        harness.Dispute = Dispute(netBalanceImpact: 0L);

        var outcome = await harness.ApplyDisputeAsync(FiatReversalEventTypes.DisputeCreated);

        outcome.Should().Be(FiatEventOutcome.Applied);
        var row = harness.Reversals.Single();
        row.Kind.Should().Be(FiatReversalKinds.Dispute);
        row.FundsWithdrawn.Should().BeFalse();
        row.AccountFlagged.Should().BeTrue();
        row.Status.Should().Be(FiatReversalStatuses.Open);
        harness.StartedSagas.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dispute_whose_money_is_gone_starts_the_unwind()
    {
        var harness = new Harness();
        harness.Dispute = Dispute(netBalanceImpact: -PaymentMinorUnits);

        var outcome = await harness.ApplyDisputeAsync(FiatReversalEventTypes.DisputeFundsWithdrawn);

        outcome.Should().Be(FiatEventOutcome.Applied);
        var row = harness.Reversals.Single();
        row.FundsWithdrawn.Should().BeTrue();
        row.Status.Should().Be(FiatReversalStatuses.Recovering);
        harness.StartedSagas.Should().ContainSingle()
            .Which.Should().Be((FiatCompensationSaga.Name, row.Id));
    }

    [Fact]
    public async Task The_balance_decides_whether_money_is_gone_not_the_event_name()
    {
        // A funds_withdrawn delivery whose dispute shows no debit must not start a clawback: an
        // event can be redelivered out of order, a balance cannot.
        var harness = new Harness();
        harness.Dispute = Dispute(netBalanceImpact: 0L);

        await harness.ApplyDisputeAsync(FiatReversalEventTypes.DisputeFundsWithdrawn);

        harness.Reversals.Single().FundsWithdrawn.Should().BeFalse();
        harness.StartedSagas.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reinstated_dispute_lifts_the_pause()
    {
        var harness = new Harness();
        harness.Dispute = Dispute(netBalanceImpact: -PaymentMinorUnits);
        await harness.ApplyDisputeAsync(FiatReversalEventTypes.DisputeCreated);

        harness.Dispute = Dispute(netBalanceImpact: 0L);
        await harness.ApplyDisputeAsync(FiatReversalEventTypes.DisputeFundsReinstated);

        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.Reinstated);
        row.AccountFlagged.Should().BeFalse();
        row.FlagClearedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_reinstatement_after_a_clawback_says_the_units_are_owed_again()
    {
        var harness = new Harness();
        harness.Dispute = Dispute(netBalanceImpact: -PaymentMinorUnits);
        await harness.ApplyDisputeAsync(FiatReversalEventTypes.DisputeFundsWithdrawn);

        // The ladder already took the units back...
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);
        harness.Reversals.Single().RecoveredQuantityBaseUnits.Should().Be(IssuedBaseUnits);

        // ...and now the provider gives the money back. Re-issuing is the on-ramp's job, so the
        // row parks with the figure on it instead of quietly keeping units nobody has a claim to.
        harness.Dispute = Dispute(netBalanceImpact: 0L);
        await harness.ApplyDisputeAsync(FiatReversalEventTypes.DisputeFundsReinstated);

        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.NeedsOperator);
        row.ReinstatementOwedBaseUnits.Should().Be(IssuedBaseUnits);
        row.AccountFlagged.Should().BeFalse();
    }

    // ── Correlation: no issuance, nothing to unwind ──

    [Fact]
    public async Task A_reversal_with_no_matching_issuance_is_recorded_and_unwinds_nothing()
    {
        var harness = new Harness { HasLot = false };

        var outcome = await harness.ApplyRefundAsync();

        outcome.Should().Be(FiatEventOutcome.Applied);
        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.Unmatched);
        row.TargetQuantityBaseUnits.Should().Be("0");
        harness.StartedSagas.Should().BeEmpty();
        harness.VerifyNoClawback();
    }

    [Fact]
    public async Task A_payment_that_was_never_credited_owes_no_units_back()
    {
        // The on-ramp refused this payment, so the refund closes money the node already owed.
        var harness = new Harness();
        harness.Lot.Status = OnRampLotStatuses.Refused;

        await harness.ApplyRefundAsync();

        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.Unmatched);
        row.TargetQuantityBaseUnits.Should().Be("0");
    }

    // ── Rung 1: the units themselves ──

    [Fact]
    public async Task Units_still_in_the_account_go_straight_back_to_the_reserve()
    {
        var harness = new Harness();
        await harness.ApplyRefundAsync();

        var result = await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        result.IsError.Should().BeFalse();
        result.Result!.Definite.Should().BeTrue();

        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.Recovered);
        row.RecoveredQuantityBaseUnits.Should().Be(IssuedBaseUnits);
        row.RecoveryPath.Should().Be(FiatReversalRecoveryPaths.Balance);

        harness.ClawedBack.Should().ContainSingle();
        harness.ClawedBack[0].Amount.Should().Be(BigInteger.Parse(IssuedBaseUnits));
        harness.ClawedBack[0].Recipient.Should().Be(ReserveAddress);

        // The partition gets its availability back; without this it would shrink by every
        // chargeback the node successfully recovered from.
        harness.Draws.Should().ContainSingle();
        harness.Draws[0].Kind.Should().Be(ReserveDrawKind.Return);
        harness.Draws[0].Partition.Should().Be("on-ramp");
    }

    [Fact]
    public async Task The_clawback_never_takes_more_than_the_person_actually_holds()
    {
        var harness = new Harness { Holding = "25000" };
        await harness.ApplyRefundAsync();

        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.ClawedBack.Single().Amount.Should().Be(new BigInteger(25000));
        var row = harness.Reversals.Single();
        row.RecoveredQuantityBaseUnits.Should().Be("25000");
        row.Status.Should().Be(FiatReversalStatuses.Recovering);
    }

    [Fact]
    public async Task A_partial_reversal_takes_back_only_its_share()
    {
        var harness = new Harness();
        harness.Charge = Charge(amountRefunded: 250L);
        await harness.ApplyRefundAsync();

        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.ClawedBack.Single().Amount.Should().Be(new BigInteger(25000));
        harness.Reversals.Single().Status.Should().Be(FiatReversalStatuses.Recovered);
    }

    [Fact]
    public async Task A_second_unwind_pass_over_a_settled_reversal_moves_nothing()
    {
        var harness = new Harness();
        await harness.ApplyRefundAsync();
        var reversalId = harness.Reversals.Single().Id;

        await harness.Compensation.RecoverAsync(reversalId);
        await harness.Compensation.RecoverAsync(reversalId);

        harness.ClawedBack.Should().HaveCount(1);
        harness.Reversals.Single().RecoveredQuantityBaseUnits.Should().Be(IssuedBaseUnits);
    }

    [Fact]
    public async Task A_recovery_move_that_already_landed_is_replayed_not_repeated()
    {
        // The move went through on an earlier attempt whose row write did not. The claim ledger
        // is what stops the next attempt from taking the same units a second time.
        var harness = new Harness();
        await harness.ApplyRefundAsync();
        var row = harness.Reversals.Single();

        harness.Idempotency.Seed(
            ReversalCompensationService.RecoveryIdempotencyKey(row, "balance", BigInteger.Zero),
            ReversalCompensationService.RecoveryOperationType,
            IdempotencyState.Completed,
            resultPayload: ReversalCompensationService.RecoveryPayload(BigInteger.Parse(IssuedBaseUnits), "CLAWBACKTX"));

        await harness.Compensation.RecoverAsync(row.Id);

        harness.VerifyNoClawback();
        var settled = harness.Reversals.Single();
        settled.RecoveredQuantityBaseUnits.Should().Be(IssuedBaseUnits);
        settled.Status.Should().Be(FiatReversalStatuses.Recovered);
    }

    [Fact]
    public async Task A_recovery_whose_outcome_is_unknown_stops_and_asks_for_a_person()
    {
        // Submitted, not confirmed. Retrying blind is how one reversal takes units twice, and a
        // clawback has no cheap reconciliation read here.
        var harness = new Harness { ClawbackConfirms = false };
        await harness.ApplyRefundAsync();

        var result = await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        result.Result!.Definite.Should().BeTrue();
        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.NeedsOperator);
        row.AccountFlagged.Should().BeTrue();
        row.RecoveredQuantityBaseUnits.Should().Be("0");
    }

    [Fact]
    public async Task An_unacknowledged_clawback_broadcast_parks_and_is_never_clawed_again_under_a_new_attempt()
    {
        // H1: the POST to algod threw after the node may have accepted the clawback. The payer still holds
        // more than is owed, so a blind attempt-suffixed retry WOULD find units to take a second time.
        var harness = new Harness { Holding = (BigInteger.Parse(IssuedBaseUnits) * 2).ToString(), ClawbackBroadcastUnacknowledged = true };
        await harness.ApplyRefundAsync();
        var id = harness.Reversals.Single().Id;

        await harness.Compensation.RecoverAsync(id);
        await harness.Compensation.RecoverAsync(id);

        harness.ClawedBack.Should().ContainSingle("an outcome-unknown broadcast is ambiguous, not a definitive failure");
        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.NeedsOperator);
        row.PendingMoveKey.Should().NotBeNull();
        (await harness.Idempotency.GetAsync(row.PendingMoveKey!, default))!.Error
            .Should().Contain(ReversalCompensationService.AmbiguousRecoveryReason).And.Contain("tx=CLAWBACKTX");
    }

    [Fact]
    public async Task An_unreadable_balance_is_retryable_and_recovers_nothing()
    {
        var harness = new Harness { HoldingReadFails = true };
        await harness.ApplyRefundAsync();

        var result = await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        result.Result!.Definite.Should().BeFalse();
        harness.VerifyNoClawback();
        harness.Reversals.Single().Status.Should().Be(FiatReversalStatuses.Recovering);
    }

    // ── Rung 2: only what can be attributed to this payment ──

    [Fact]
    public async Task Nothing_unattributable_is_ever_unwound()
    {
        // The person spent the units, and no per-purchase link exists yet. Taking back an
        // arbitrary hold they benefit from would rob whoever honestly funded it.
        var harness = new Harness { Holding = "0" };
        await harness.ApplyRefundAsync();

        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.VerifyNoClawback();
        harness.Purchases.Verify(
            store => store.RecoverExchangePurchaseAsync(It.IsAny<ExchangePurchaseRecovery>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Reversals.Single().RecoveredQuantityBaseUnits.Should().Be("0");
    }

    [Fact]
    public async Task An_open_commitment_this_payment_funded_is_given_back()
    {
        var harness = new Harness { Holding = "0" };
        harness.Provenance.Claims.Add(new ReversedPurchaseClaim(
            ReversedPurchaseKind.OpenHold,
            ChainType,
            SourceQuantityBaseUnits: BigInteger.Parse(IssuedBaseUnits),
            HoldId: "hold-1",
            SourceId: "source-1"));

        await harness.ApplyRefundAsync();
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.Purchases.Verify(
            store => store.RecoverExchangePurchaseAsync(
                It.Is<ExchangePurchaseRecovery>(request => request.SourceId == "source-1"
                    && request.AttributedSourceUnits == IssuedBaseUnits && request.SubjectUserId == SubjectUserId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var row = harness.Reversals.Single();
        row.RecoveryPath.Should().Be(FiatReversalRecoveryPaths.Hold);
        row.Status.Should().Be(FiatReversalStatuses.Recovered);
    }

    [Fact]
    public async Task A_purchase_recovery_whose_row_save_was_lost_is_counted_once_and_never_taken_again()
    {
        var harness = new Harness { Holding = "0" };
        harness.Provenance.Claims.Add(new ReversedPurchaseClaim(ReversedPurchaseKind.OpenHold, ChainType,
            SourceQuantityBaseUnits: BigInteger.Parse(IssuedBaseUnits), HoldId: "hold-1", SourceId: "source-1"));
        await harness.ApplyRefundAsync();
        harness.Ledger.LostSaves = 1;

        var lost = await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);
        var retried = await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        lost.Result!.Definite.Should().BeFalse("the pass that recovered could not record it");
        retried.Result!.Definite.Should().BeTrue();
        harness.CommittedRecoveries.Should().ContainSingle().Which.CreditedSourceUnits.Should().Be(IssuedBaseUnits);
        var row = harness.Reversals.Single();
        row.RecoveredQuantityBaseUnits.Should().Be(IssuedBaseUnits, "the committed recovery is counted exactly once");
        row.PurchaseRecoveredBaseUnits.Should().Be(IssuedBaseUnits);
        row.Status.Should().Be(FiatReversalStatuses.Recovered);
    }

    [Fact]
    public async Task Units_this_payment_bought_are_taken_back_with_the_reserves_authority()
    {
        var harness = new Harness { Holding = "0", PurchasedAssetUnits = 500 };
        harness.Provenance.Claims.Add(new ReversedPurchaseClaim(
            ReversedPurchaseKind.IssuedAsset,
            ChainType,
            SourceQuantityBaseUnits: BigInteger.Parse(IssuedBaseUnits),
            AssetId: "2002",
            AssetQuantityBaseUnits: new BigInteger(500),
            HolderAddress: HolderAddress,
            SourceId: "source-1"));

        await harness.ApplyRefundAsync();
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.ClawedBack.Should().ContainSingle();
        harness.ClawedBack[0].AssetId.Should().Be("2002");
        harness.ClawedBack[0].Amount.Should().Be(new BigInteger(500));
        harness.Reversals.Single().RecoveryPath.Should().Be(FiatReversalRecoveryPaths.Purchase);
    }

    [Fact]
    public async Task A_lost_row_save_never_claws_the_balance_twice_even_when_more_becomes_owed()
    {
        var harness = new Harness();
        harness.Charge = Charge(amountRefunded: 300L);
        await harness.ApplyRefundAsync();
        harness.Ledger.LostSaves = 1;
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        // A redelivery raises what is owed before the next pass; the first move is replayed, never repeated.
        harness.Charge = Charge(amountRefunded: 600L);
        await harness.ApplyRefundAsync();
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.ClawedBack.Select(call => call.Amount).Should().Equal(new BigInteger(30000), new BigInteger(30000));
        var row = harness.Reversals.Single();
        row.RecoveredQuantityBaseUnits.Should().Be("60000");
        row.Status.Should().Be(FiatReversalStatuses.Recovered);
    }

    [Fact]
    public async Task A_lost_row_save_never_claws_a_purchased_asset_twice()
    {
        var harness = new Harness { Holding = "0" };
        harness.Provenance.Claims.Add(DirectBuy());
        await harness.ApplyRefundAsync();
        harness.Ledger.LostSaves = 1;

        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.ClawedBack.Should().ContainSingle().Which.Amount.Should().Be(new BigInteger(1000));
        var row = harness.Reversals.Single();
        row.RecoveredQuantityBaseUnits.Should().Be(IssuedBaseUnits, "the completed clawback is counted exactly once");
        row.Status.Should().Be(FiatReversalStatuses.Recovered);
    }

    [Fact]
    public async Task A_partial_refund_claws_only_its_share_of_a_direct_buy_and_never_more_than_was_delivered()
    {
        var harness = new Harness { Holding = "0" };
        harness.Provenance.Claims.Add(DirectBuy());
        harness.Charge = Charge(amountRefunded: 300L);
        await harness.ApplyRefundAsync();
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);
        harness.Charge = Charge(amountRefunded: 600L);
        await harness.ApplyRefundAsync();
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.ClawedBack.Select(call => call.Amount).Should().Equal(new BigInteger(300), new BigInteger(300));
        harness.Reversals.Single().RecoveredQuantityBaseUnits.Should().Be("60000",
            "each refund is credited exactly the share it clawed");
    }

    [Fact]
    public async Task Two_partial_refunds_and_a_repeated_pass_leave_exactly_the_unrefunded_units_cashable()
    {
        var harness = new Harness();
        harness.Charge = Charge(amountRefunded: 300L);
        await harness.ApplyRefundAsync();
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);
        harness.Charge = Charge(amountRefunded: 600L);
        await harness.ApplyRefundAsync();
        harness.Ledger.LostSaves = 1;
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);
        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        var lot = harness.Lots.Rows.Single();
        lot.ConsumedBaseUnits.Should().Be("60000", "a cumulative target is marked once, not added on top of itself");
        (BigInteger.Parse(lot.QuantityBaseUnits) - BigInteger.Parse(lot.ConsumedBaseUnits))
            .Should().Be(new BigInteger(40000), "the cash-out rule may still pay out what was never refunded");
    }

    private static ReversedPurchaseClaim DirectBuy() => new(ReversedPurchaseKind.IssuedAsset, ChainType,
        SourceQuantityBaseUnits: BigInteger.Parse(IssuedBaseUnits), AssetId: "2002",
        AssetQuantityBaseUnits: new BigInteger(1000), HolderAddress: HolderAddress, SourceId: "direct-buy");

    // ── Rung 3: the platform's own pocket, recorded and enforced ──

    [Fact]
    public async Task A_shortfall_is_absorbed_as_a_recorded_loss_and_pauses_the_account()
    {
        var harness = new Harness { Holding = "40000" };
        await harness.ApplyRefundAsync();
        var reversalId = harness.Reversals.Single().Id;

        await harness.Compensation.RecoverAsync(reversalId);
        await harness.Compensation.SettleShortfallAsync(reversalId);

        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.PartiallyRecovered);
        row.RecoveredQuantityBaseUnits.Should().Be("40000");
        row.AbsorbedQuantityBaseUnits.Should().Be("60000");
        row.RecoveryPath.Should().Be(FiatReversalRecoveryPaths.Mixed);
        row.AccountFlagged.Should().BeTrue();
        row.StatusReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Nothing_recoverable_at_all_is_absorbed_in_full()
    {
        var harness = new Harness { Holding = "0" };
        await harness.ApplyRefundAsync();
        var reversalId = harness.Reversals.Single().Id;

        await harness.Compensation.RecoverAsync(reversalId);
        await harness.Compensation.SettleShortfallAsync(reversalId);

        var row = harness.Reversals.Single();
        row.Status.Should().Be(FiatReversalStatuses.Absorbed);
        row.AbsorbedQuantityBaseUnits.Should().Be(IssuedBaseUnits);
        row.RecoveryPath.Should().Be(FiatReversalRecoveryPaths.Reserve);
    }

    [Fact]
    public async Task A_flow_that_absorbs_small_reversals_never_touches_the_account()
    {
        var harness = new Harness();
        harness.Options.Default.AbsorbAtOrBelowMinorUnits = PaymentMinorUnits;
        await harness.ApplyRefundAsync();

        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.VerifyNoClawback();
        harness.Reversals.Single().RecoveredQuantityBaseUnits.Should().Be("0");
    }

    // ── The issuance is consumed, so a later cash-out cannot hand the units out again ──

    [Fact]
    public async Task A_full_reversal_marks_the_issuance_reversed()
    {
        var harness = new Harness();
        await harness.ApplyRefundAsync();

        await harness.Compensation.RecoverAsync(harness.Reversals.Single().Id);

        harness.Lots.Rows.Single().Status.Should().Be(OnRampLotStatuses.Reversed);
    }

    [Fact]
    public async Task A_partial_reversal_raises_the_issuances_consumed_watermark()
    {
        var harness = new Harness();
        harness.Charge = Charge(amountRefunded: 250L);
        await harness.ApplyRefundAsync();
        var reversalId = harness.Reversals.Single().Id;

        await harness.Compensation.RecoverAsync(reversalId);

        var lot = harness.Lots.Rows.Single();
        lot.Status.Should().Be(OnRampLotStatuses.Issued);
        lot.ConsumedBaseUnits.Should().Be("25000");
    }

    // ── The pause is a control: the value gate reads it ──

    [Fact]
    public async Task A_paused_account_cannot_take_another_value_action()
    {
        var reversals = new FakeReversalLedger { FlaggedUsers = { SubjectUserId } };
        var inner = new Mock<IRealValueKycGate>();
        inner.Setup(g => g.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));

        var gate = new RiskHoldValueGate(
            inner.Object, reversals, NullLogger<RiskHoldValueGate>.Instance);

        var verdict = await gate.RequireCurrentApprovalAsync(SubjectUserId);

        verdict.IsError.Should().BeTrue();
        verdict.Message.Should().Be(RiskHoldValueGate.RiskHoldMessage);
        inner.Verify(
            g => g.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_clear_account_still_has_to_pass_the_identity_gate()
    {
        var reversals = new FakeReversalLedger();
        var inner = new Mock<IRealValueKycGate>();
        inner.Setup(g => g.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure("Identity verification is not current."));

        var gate = new RiskHoldValueGate(
            inner.Object, reversals, NullLogger<RiskHoldValueGate>.Instance);

        var verdict = await gate.RequireCurrentApprovalAsync(SubjectUserId);

        verdict.IsError.Should().BeTrue();
        verdict.Message.Should().Contain("Identity verification");
    }

    [Fact]
    public async Task An_unreadable_reversal_ledger_refuses_rather_than_assuming_no_pause()
    {
        var reversals = new FakeReversalLedger { FlagReadFails = true };
        var inner = new Mock<IRealValueKycGate>();
        inner.Setup(g => g.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));

        var gate = new RiskHoldValueGate(
            inner.Object, reversals, NullLogger<RiskHoldValueGate>.Instance);

        var verdict = await gate.RequireCurrentApprovalAsync(SubjectUserId);

        verdict.IsError.Should().BeTrue();
        verdict.Message.Should().Be(RiskHoldValueGate.UnreadableMessage);
    }

    // ── The saga is shaped the way the ladder needs ──

    [Fact]
    public void The_compensation_saga_declares_an_escalation_on_every_forward_step()
    {
        var definition = FiatCompensationSagaDefinition.Build(maxAttempts: 4);

        definition.Name.Should().Be(FiatCompensationSaga.Name);
        definition.ForwardSteps.Select(s => s.Name).Should().Equal(
            FiatCompensationSaga.RecoverStepName, FiatCompensationSaga.SettleStepName);

        foreach (var step in definition.ForwardSteps)
        {
            step.CompensationStepName.Should().Be(FiatCompensationSaga.EscalateStepName);
            step.RetryPolicy.MaxAttempts.Should().Be(4);
        }

        definition.FindStep(FiatCompensationSaga.EscalateStepName)!
            .CompensationStepName.Should().BeNull();
        definition.NextForwardStep(FiatCompensationSaga.SettleStepName).Should().BeNull();
    }

    [Fact]
    public void One_reversal_is_one_saga_instance()
    {
        // The correlation key IS the reversal record id, so a redelivery addresses the same
        // instance and the step idempotency keys are stable across restarts.
        FiatCompensationSaga.CorrelationKeyFor("abc").Should().Be("abc");
        SagaKeys.StepIdempotencyKey("abc", FiatCompensationSaga.RecoverStepName)
            .Should().Be($"saga:abc:{FiatCompensationSaga.RecoverStepName}");
    }

    [Fact]
    public void A_refund_and_a_dispute_on_one_charge_are_two_reversals()
    {
        var refund = FiatReversal.RecordIdFor("stripe", FiatReversal.RefundScope, ChargeId);
        var dispute = FiatReversal.RecordIdFor("stripe", FiatReversal.DisputeScope, DisputeId);
        var repeat = FiatReversal.RecordIdFor("stripe", FiatReversal.RefundScope, ChargeId);

        refund.Should().NotBe(dispute);
        refund.Should().Be(repeat);
    }

    [Fact]
    public void The_recovery_claim_key_changes_only_when_more_is_owed()
    {
        var row = new FiatReversal { Id = "rev-1" };

        var first = ReversalCompensationService.RecoveryIdempotencyKey(row, "balance", new BigInteger(100));
        var same = ReversalCompensationService.RecoveryIdempotencyKey(row, "balance", new BigInteger(100));
        var more = ReversalCompensationService.RecoveryIdempotencyKey(row, "balance", new BigInteger(140));

        first.Should().Be(same);
        first.Should().NotBe(more);
    }

    // ── The stub is honest about being one ──

    [Fact]
    public async Task The_default_purchase_provenance_reports_that_it_knows_nothing()
    {
        var provenance = new UnavailableReversedPurchaseProvenance();

        provenance.IsAvailable.Should().BeFalse();
        var claims = await provenance.ListForLotAsync("lot-1", SubjectUserId);
        claims.Result.Should().BeEmpty();
        claims.Message.Should().Be(UnavailableReversedPurchaseProvenance.UnavailableReason);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ChargeReversalFacts Charge(long amountRefunded) => new(
        ChargeId: ChargeId,
        PaymentIntentId: PaymentIntentId,
        AmountMinorUnits: PaymentMinorUnits,
        AmountRefundedMinorUnits: amountRefunded,
        Currency: "usd",
        Refunded: amountRefunded >= PaymentMinorUnits,
        Disputed: false,
        Livemode: false,
        IntentId: IntentId,
        SubjectUserId: SubjectUserId);

    private static DisputeReversalFacts Dispute(long netBalanceImpact) => new(
        DisputeId: DisputeId,
        ChargeId: ChargeId,
        AmountMinorUnits: PaymentMinorUnits,
        Currency: "usd",
        Status: netBalanceImpact < 0 ? "lost" : "warning_needs_response",
        Livemode: false,
        NetBalanceImpactMinorUnits: netBalanceImpact);

    /// <summary>One clawback the ladder actually performed.</summary>
    private sealed record ClawbackCall(string AssetId, string Holder, string Recipient, BigInteger Amount);

    private sealed class Harness
    {
        public Harness()
        {
            Charge = ReversalTests.Charge(amountRefunded: PaymentMinorUnits);
            Dispute = ReversalTests.Dispute(netBalanceImpact: -PaymentMinorUnits);
            Lot = BuildLot();
            Lots.Seed(Lot);
        }

        public ChargeReversalFacts Charge { get; set; }

        public DisputeReversalFacts Dispute { get; set; }

        public OnRampLot Lot { get; }

        public bool HasLot { get; init; } = true;

        public string Holding { get; init; } = IssuedBaseUnits;

        /// <summary>Units a purchase funded by the whole issuance delivered; the store's clawback contract scales from it.</summary>
        public BigInteger PurchasedAssetUnits { get; init; } = 1000;

        public bool HoldingReadFails { get; init; }

        public bool ClawbackConfirms { get; init; } = true;

        /// <summary>The clawback reached algod and the POST failed: the provider's outcome-unknown error.</summary>
        public bool ClawbackBroadcastUnacknowledged { get; init; }

        public ReversalOptions Options { get; } = new();

        public OnRampOptions OnRamp { get; } = new();

        public FakeReversalLedger Ledger { get; } = new();

        public FakeLotLedger Lots { get; } = new();

        public FakeProvenance Provenance { get; } = new();

        public FakeIdempotencyStore Idempotency { get; } = new();

        public Mock<IExchangeHoldSourceStore> Purchases { get; } = new();

        public List<ExchangePurchaseRecoveryResult> CommittedRecoveries { get; } = new();

        public List<(string Saga, string Correlation)> StartedSagas { get; } = new();

        public List<ClawbackCall> ClawedBack { get; } = new();

        public List<ReserveDrawRequest> Draws { get; } = new();

        public IReadOnlyCollection<FiatReversal> Reversals => Ledger.Rows;

        public void VerifyNoClawback() => ClawedBack.Should().BeEmpty();

        public Task<FiatEventOutcome> ApplyRefundAsync()
            => BuildEventHandler().ApplyAsync(BuildContext(FiatReversalEventTypes.ChargeRefunded));

        public Task<FiatEventOutcome> ApplyDisputeAsync(string eventType)
            => BuildEventHandler().ApplyAsync(BuildContext(eventType));

        public IReversalCompensation Compensation => BuildCompensation();

        private FiatOptions BuildFiatOptions()
        {
            var fiat = new FiatOptions
            {
                Enabled = true,
                Environment = RailEnvironmentNames.Test,
                Test = { IsEnabled = true, SecretKey = "sk_test_x", WebhookSecret = "whsec_x" },
            };
            fiat.Checkout.Currency = "usd";
            return fiat;
        }

        private FiatReversalEventHandler BuildEventHandler()
        {
            var facts = new Mock<IReversalFactSource>();
            facts.Setup(f => f.GetChargeAsync(
                    It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<ChargeReversalFacts>.Success(Charge));
            facts.Setup(f => f.GetDisputeForPaymentAsync(
                    It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<DisputeReversalFacts>.Success(Dispute));
            facts.Setup(f => f.GetDisputeAsync(
                    It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<DisputeReversalFacts>.Success(Dispute));

            var lookup = new Mock<IOnRampLotLookup>();
            lookup.Setup(l => l.GetByChargeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AZOAResult<OnRampLot?> { Result = HasLot ? Lots.Rows.First() : null });
            lookup.Setup(l => l.GetByPaymentIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AZOAResult<OnRampLot?> { Result = HasLot ? Lots.Rows.First() : null });

            var sagas = new Mock<ISagaCoordinator>();
            sagas.Setup(s => s.StartAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<FiatCompensationPayload>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, FiatCompensationPayload, CancellationToken>(
                    (saga, correlation, _, _) => StartedSagas.Add((saga, correlation)))
                .Returns(Task.CompletedTask);

            return new FiatReversalEventHandler(
                BuildFiatOptions(),
                Options,
                facts.Object,
                Ledger,
                lookup.Object,
                sagas.Object,
                NullLogger<FiatReversalEventHandler>.Instance,
                new FixedClock(Now));
        }

        private ReversalCompensationService BuildCompensation()
        {
            var reserve = new Mock<IPlatformReserve>();
            reserve.Setup(r => r.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string chain, CancellationToken _) =>
                    AZOAResult<ReserveAccount>.Success(new ReserveAccount(chain, ReserveAddress, true)));
            reserve.Setup(r => r.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ReserveDrawRequest request, CancellationToken _) =>
                {
                    Draws.Add(request);
                    return AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(
                        request.AssetId, request.Partition, request.Kind, request.Amount,
                        ReservePartitionCounters.Declaring(new BigInteger(1_000_000)), 1, false));
                });

            // Process edge with the store's contract: each step takes only what this reversal has not yet taken.
            Purchases.Setup(store => store.ListReversalRecoveriesAsync(
                    It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => CommittedRecoveries.ToList());
            Purchases.Setup(store => store.ReserveExchangeClawbackAsync(
                    It.IsAny<ExchangePurchaseRecovery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ExchangePurchaseRecovery request, CancellationToken _) =>
                {
                    var already = CommittedRecoveries.Where(step => step.SourceId == request.SourceId)
                        .Aggregate(BigInteger.Zero, (total, step) => total + BigInteger.Parse(step.CreditedSourceUnits));
                    var delta = BigInteger.Min(BigInteger.Parse(request.AttributedSourceUnits), BigInteger.Parse(request.ClaimSourceUnits)) - already;
                    var clawed = CommittedRecoveries.Where(step => step.Clawback && step.SourceId == request.SourceId)
                        .Aggregate(BigInteger.Zero, (total, step) => total + BigInteger.Parse(step.RecoveredHoldUnits));
                    var spent = BigInteger.Parse(request.ClaimSourceUnits);
                    var units = BigInteger.Min((PurchasedAssetUnits * delta + spent - 1) / spent, PurchasedAssetUnits - clawed);
                    if (delta <= BigInteger.Zero || units <= BigInteger.Zero) return null;
                    var step = new ExchangePurchaseRecoveryResult($"clawback-{CommittedRecoveries.Count}", request.SourceId,
                        units.ToString(), delta.ToString(), string.Empty, null, Clawback: true);
                    CommittedRecoveries.Add(step);
                    return step;
                });
            Purchases.Setup(store => store.RecoverExchangePurchaseAsync(
                    It.IsAny<ExchangePurchaseRecovery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ExchangePurchaseRecovery request, CancellationToken _) =>
                {
                    var already = CommittedRecoveries.Where(step => step.SourceId == request.SourceId)
                        .Aggregate(BigInteger.Zero, (total, step) => total + BigInteger.Parse(step.CreditedSourceUnits));
                    var delta = BigInteger.Min(BigInteger.Parse(request.AttributedSourceUnits), BigInteger.Parse(request.ClaimSourceUnits)) - already;
                    if (delta <= BigInteger.Zero) return null;
                    var step = new ExchangePurchaseRecoveryResult($"receipt-{CommittedRecoveries.Count}", request.SourceId, "1",
                        delta.ToString(), "liability", null);
                    CommittedRecoveries.Add(step);
                    return step;
                });

            var asa = new Mock<IAlgorandASAModule>();
            asa.Setup(a => a.GetAssetHoldingAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string assetId, string holder, CancellationToken _) => HoldingReadFails
                    ? AZOAResult<string>.Failure("The node could not be reached.")
                    : AZOAResult<string>.Success((BigInteger.Parse(Holding) - ClawedBack
                        .Where(call => call.AssetId == assetId && call.Holder == holder)
                        .Aggregate(BigInteger.Zero, (total, call) => total + call.Amount)).ToString()));
            asa.Setup(a => a.ClawbackAssetAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RailAmount>(),
                    It.IsAny<SigningContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string assetId, string holder, string recipient, RailAmount amount,
                    SigningContext _, string? __, CancellationToken ___) =>
                {
                    ClawedBack.Add(new ClawbackCall(assetId, holder, recipient, amount.BaseUnits));
                    if (ClawbackBroadcastUnacknowledged)
                        return AZOAResult<string>.Failure(
                            "Broadcast failed to revoke ASA (tx CLAWBACKTX); reconcile against chain truth before re-sending: timeout");
                    return ClawbackConfirms
                        ? AZOAResult<string>.Success("CLAWBACKTX", "confirmed in round 42")
                        : AZOAResult<string>.Success(
                            "CLAWBACKTX", OperationStatus.PendingConfirmationMarker);
                });

            var module = asa.Object;
            var provider = new Mock<IBlockchainProvider>();
            provider.Setup(p => p.TryGetModule<IAlgorandASAModule>(out It.Ref<IAlgorandASAModule?>.IsAny))
                .Returns((out IAlgorandASAModule? m) => { m = module; return true; });

            var factory = new Mock<IBlockchainProviderFactory>();
            factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(provider.Object);
            factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);

            var wallets = new Mock<IWalletStore>();
            wallets.Setup(w => w.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>>
                {
                    Result = new[]
                    {
                        Mock.Of<IWallet>(x => x.Id == Guid.NewGuid() && x.UserId == SubjectUserId
                                              && x.ChainType == ChainType && x.Address == HolderAddress),
                    },
                });

            return new ReversalCompensationService(
                Ledger,
                Lots,
                reserve.Object,
                Purchases.Object,
                Provenance,
                factory.Object,
                wallets.Object,
                Idempotency,
                Options,
                OnRamp,
                NullLogger<ReversalCompensationService>.Instance,
                new FixedClock(Now));
        }

        private FiatEventContext BuildContext(string eventType) => new(
            new FiatEvent
            {
                Id = "event-row",
                Provider = FiatRailIds.StripeProviderKey,
                ProviderEventId = $"evt_{eventType}_{Guid.NewGuid():N}",
                EventType = eventType,
                Environment = RailEnvironmentNames.Test,
                Status = FiatEventStatuses.Received,
                PayloadDigest = "digest",
            },
            eventType,
            SessionId,
            PaymentIntentId,
            string.Equals(eventType, FiatReversalEventTypes.ChargeRefunded, StringComparison.Ordinal)
                ? ChargeId
                : null,
            null,
            null,
            RailEnvironment.Test);

        private static OnRampLot BuildLot() => new()
        {
            Id = OnRampLot.RecordIdFor(IntentId.ToString("N")),
            IntentId = IntentId.ToString("N"),
            SubjectUserId = SubjectUserId.ToString("N"),
            ChainType = ChainType,
            AssetId = AssetId,
            QuantityBaseUnits = IssuedBaseUnits,
            GrossQuantityBaseUnits = IssuedBaseUnits,
            ConsumedBaseUnits = "0",
            FiatMinorUnits = PaymentMinorUnits,
            CurrencyCode = "USD",
            SourceCheckoutSessionId = SessionId,
            SourcePaymentIntentId = PaymentIntentId,
            SourceChargeId = ChargeId,
            Status = OnRampLotStatuses.Issued,
            IssuedAt = Now.AddDays(-1),
            Version = 0,
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
        };
    }

    /// <summary>In-memory reversal ledger with the production CAS contract.</summary>
    private sealed class FakeReversalLedger : IFiatReversalLedger
    {
        private readonly Dictionary<string, FiatReversal> _rows = new(StringComparer.Ordinal);

        public HashSet<Guid> FlaggedUsers { get; } = new();

        public bool FlagReadFails { get; init; }

        public IReadOnlyCollection<FiatReversal> Rows => _rows.Values;

        public Task<AZOAResult<FiatReversalClaim>> ClaimAsync(
            FiatReversal candidate, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(candidate.Id, out var existing))
                return Task.FromResult(AZOAResult<FiatReversalClaim>.Success(
                    new FiatReversalClaim(Clone(existing), Created: false)));

            _rows[candidate.Id] = Clone(candidate);
            return Task.FromResult(AZOAResult<FiatReversalClaim>.Success(
                new FiatReversalClaim(Clone(candidate), Created: true)));
        }

        public Task<AZOAResult<FiatReversal?>> GetAsync(string recordId, CancellationToken ct = default)
        {
            _rows.TryGetValue(recordId, out var row);
            return Task.FromResult(new AZOAResult<FiatReversal?> { Result = row is null ? null : Clone(row) });
        }

        /// <summary>Saves still to lose a version race, as when a concurrent writer moved the row first.</summary>
        public int LostSaves { get; set; }

        public Task<AZOAResult<FiatReversal>> UpdateIfVersionAsync(
            FiatReversal row, long expectedVersion, CancellationToken ct = default)
        {
            if (LostSaves > 0)
            {
                LostSaves--;
                return Task.FromResult(AZOAResult<FiatReversal>.Failure("Version conflict."));
            }
            if (!_rows.TryGetValue(row.Id, out var current) || current.Version != expectedVersion)
                return Task.FromResult(AZOAResult<FiatReversal>.Failure("Version conflict."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<FiatReversal>.Success(Clone(row)));
        }

        public Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListOpenAsync(
            int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<FiatReversal>>.Success(
                _rows.Values.Where(r => !FiatReversalStatuses.IsSettled(r.Status))
                    .Select(Clone).ToList()));

        public Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListForUserAsync(
            Guid userId, int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<FiatReversal>>.Success(
                _rows.Values.Where(r => r.SubjectUserId == userId.ToString("N"))
                    .Select(Clone).ToList()));

        public Task<AZOAResult<bool>> HasActiveFlagAsync(Guid userId, CancellationToken ct = default)
        {
            if (FlagReadFails)
                return Task.FromResult(AZOAResult<bool>.Failure("The ledger could not be read."));

            var flagged = FlaggedUsers.Contains(userId)
                || _rows.Values.Any(r => r.AccountFlagged && r.SubjectUserId == userId.ToString("N"));
            return Task.FromResult(AZOAResult<bool>.Success(flagged));
        }

        // A JSON round-trip, like the lot ledger: a hand-listed copy silently drops new columns.
        private static FiatReversal Clone(FiatReversal row)
            => System.Text.Json.JsonSerializer.Deserialize<FiatReversal>(System.Text.Json.JsonSerializer.Serialize(row))!;
    }

    /// <summary>In-memory on-ramp lot ledger; only the reads the reversal pipeline uses matter.</summary>
    private sealed class FakeLotLedger : IOnRampLotLedger
    {
        private readonly Dictionary<string, OnRampLot> _rows = new(StringComparer.Ordinal);

        public IReadOnlyCollection<OnRampLot> Rows => _rows.Values;

        public void Seed(OnRampLot row) => _rows[row.Id] = row;

        public Task<AZOAResult<OnRampLotClaim>> ClaimAsync(
            OnRampLot candidate, CancellationToken ct = default)
        {
            _rows[candidate.Id] = candidate;
            return Task.FromResult(AZOAResult<OnRampLotClaim>.Success(
                new OnRampLotClaim(candidate, Created: true)));
        }

        public Task<AZOAResult<OnRampLot?>> GetByIntentAsync(string intentId, CancellationToken ct = default)
        {
            // A COPY, like the production store: handing out the stored instance would let a
            // caller mutate the row it is about to compare-and-swap against, and every CAS in
            // these tests would pass for the wrong reason.
            _rows.TryGetValue(OnRampLot.RecordIdFor(intentId), out var row);
            return Task.FromResult(new AZOAResult<OnRampLot?> { Result = row is null ? null : Clone(row) });
        }

        public Task<AZOAResult<OnRampLot>> UpdateIfVersionAsync(
            OnRampLot row, long expectedVersion, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(row.Id, out var current) || current.Version != expectedVersion)
                return Task.FromResult(AZOAResult<OnRampLot>.Failure("Version conflict."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<OnRampLot>.Success(Clone(row)));
        }

        private static OnRampLot Clone(OnRampLot row) => System.Text.Json.JsonSerializer.Deserialize<OnRampLot>(System.Text.Json.JsonSerializer.Serialize(row))!;

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListIssuedForUserAsync(
            Guid userId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                _rows.Values.Where(r => r.Status == OnRampLotStatuses.Issued).ToList()));

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListSoldSinceAsync(DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<OnRampLot>>.Success(Array.Empty<OnRampLot>()));

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListNeedingOperatorAsync(
            int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                _rows.Values.Where(r => OnRampLotStatuses.NeedsOperator(r.Status)).ToList()));
    }

    /// <summary>A provenance source the test can switch on; empty by default, like the shipped stub.</summary>
    private sealed class FakeProvenance : IReversedPurchaseProvenance
    {
        public List<ReversedPurchaseClaim> Claims { get; } = new();

        public bool IsAvailable => Claims.Count > 0;

        public Task<AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>> ListForLotAsync(
            string lotId, Guid subjectUserId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<ReversedPurchaseClaim>>.Success(Claims));
    }

    /// <summary>A clock that does not move, so a timestamp assertion is exact.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
