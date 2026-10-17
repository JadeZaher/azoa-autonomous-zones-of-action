using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// Operator resolutions of parked cash-outs (RV-L3 M1, M2, L2) and their D-8 debit sync, through
/// <see cref="OffRampService.ResolveForOperatorAsync"/>. See Services/Fiat/OffRamp/AGENTS.md §operator-resolution.
/// </summary>
public partial class OffRampTests
{
    private static readonly Guid OperatorActor = new("99999999999999999999999999999999");

    private static Task<AZOAResult<FiatPayout>> ReturnUnits(Harness harness, string payoutId)
        => harness.Service.ResolveForOperatorAsync(payoutId, PayoutResolution.ReturnUnits, OperatorActor, "reconciled in Stripe");

    [Fact]
    public async Task Returning_units_fences_the_pay_leg_so_a_send_that_read_the_row_earlier_cannot_pay()
    {
        // M1 interleaving (a): a send step read UnitsCollected, the row was parked, the operator returned the units.
        var harness = new Harness { Holding = "50000" };
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        var staleView = harness.Payouts.Rows.Single();
        await harness.Service.ParkForOperatorAsync(payoutId, "operator review");

        var resolved = await ReturnUnits(harness, payoutId);
        harness.Payouts.Seed(staleView);
        await harness.Service.SendMoneyAsync(payoutId);

        resolved.Result!.Status.Should().Be(FiatPayoutStatuses.Returned);
        harness.SentTransfers.Should().BeEmpty("the late send lost the fenced pay claim");
        (await harness.Idempotency.GetAsync(OffRampService.MoveIdempotencyKey(payoutId, "pay"), default))!
            .Error.Should().Be(OffRampService.OperatorFenceReason);
    }

    [Fact]
    public async Task A_fence_claim_a_crash_left_in_progress_is_never_replayed_as_a_lost_payment()
    {
        // The resolution died between taking the pay fence and failing it; the stale-pay replay must not send.
        var harness = new Harness { Holding = "50000" };
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        harness.Idempotency.SeedAged(OffRampService.MoveIdempotencyKey(payoutId, "pay"), OffRampService.OperatorFenceOperationType,
            IdempotencyState.InProgress, DateTime.UtcNow.AddMinutes(-10));
        harness.Clock.Now = DateTimeOffset.UtcNow;

        await harness.Service.SendMoneyAsync(payoutId);

        harness.SentTransfers.Should().BeEmpty();
    }

    [Fact]
    public async Task An_operator_return_of_a_collected_tenant_cash_out_releases_its_debit_once()
    {
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.ParkForOperatorAsync(payoutId, "operator review");

        var resolved = await ReturnUnits(harness, payoutId);
        await harness.Funding.SyncAsync(harness.Payouts.Rows.Single());

        resolved.Result!.Status.Should().Be(FiatPayoutStatuses.Returned);
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Released);
        var view = await FloatOf(harness);
        view.AvailableMinorUnits.Should().Be(500);
        view.Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task Returning_units_is_refused_while_a_pay_attempt_may_still_be_in_flight()
    {
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.ParkForOperatorAsync(payoutId, "operator review");
        await harness.Idempotency.TryClaimAsync(OffRampService.MoveIdempotencyKey(payoutId, "pay"), OffRampService.PayoutOperationType, default);

        var resolved = await ReturnUnits(harness, payoutId);

        resolved.Code.Should().Be(AzoaErrorCodes.Conflict);
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        harness.PreparedCount.Should().Be(0, "no units were handed back");
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Reserved);
    }

    [Fact]
    public async Task Closing_a_never_collected_cash_out_fences_the_collect_leg_against_a_late_collect()
    {
        // M1: a collect step that read Requested before the park must not take units from a Refused row.
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        var staleView = harness.Payouts.Rows.Single();
        await harness.Service.ParkForOperatorAsync(payoutId, "operator review");

        var resolved = await ReturnUnits(harness, payoutId);
        harness.Payouts.Seed(staleView);
        await harness.Service.CollectUnitsAsync(payoutId);

        resolved.Result!.Status.Should().Be(FiatPayoutStatuses.Refused);
        harness.Transfers.Should().BeEmpty("the late collect lost the fenced claim");
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Released);
    }

    [Fact]
    public async Task A_collect_whose_broadcast_outcome_is_unknown_is_never_closed_as_not_collected()
    {
        // M2: the transfer was sent and not acknowledged; only chain truth may decide.
        var harness = new Harness { Holding = "50000", TransferOutcomeUnknown = true };
        await harness.RequestAsync(amountMinorUnits: 500);
        var payoutId = harness.Payouts.Rows.Single().Id;

        await harness.Service.CollectUnitsAsync(payoutId);
        var parked = harness.Payouts.Rows.Single();
        var unknown = await ReturnUnits(harness, payoutId);
        harness.ChainVerdict = ChainConfirmation.Confirmed;
        var landed = await ReturnUnits(harness, payoutId);

        parked.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        (await harness.Idempotency.GetAsync(OffRampService.MoveIdempotencyKey(payoutId, "collect"), default))!
            .Error.Should().Contain(" tx=AMBIGTX");
        unknown.Code.Should().Be(AzoaErrorCodes.Conflict, "the chain has not said whether the units moved");
        landed.Result!.Status.Should().Be(FiatPayoutStatuses.Returned, "chain truth says they moved, so they are given back");
        harness.Transfers.Count(move => move.From == HolderAddress).Should().Be(1, "the collect was never re-sent");
        harness.Transfers.Count(move => move.To == HolderAddress).Should().Be(1);
    }

    [Fact]
    public async Task A_409_park_is_recorded_paid_from_the_concurrent_transfer_and_finalizes_the_debit_once_paid()
    {
        // M5: the 409 means a concurrent same-key request created the transfer, and the group lists it.
        var harness = Funded(500);
        harness.TransferConflict = true;
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.SendMoneyAsync(payoutId);

        var returnRefused = await ReturnUnits(harness, payoutId);
        var recorded = await harness.Service.ResolveForOperatorAsync(payoutId, PayoutResolution.RecordPaid, OperatorActor, "transfer seen in Stripe");
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Reserved, "bank-pending is not paid yet");
        harness.TransferConflict = false;
        await harness.Service.SendMoneyAsync(payoutId);

        returnRefused.Code.Should().Be(AzoaErrorCodes.Conflict, "money left the platform");
        recorded.Result!.Status.Should().Be(FiatPayoutStatuses.BankPending);
        harness.Payouts.Rows.Single().Status.Should().Be(FiatPayoutStatuses.Paid);
        harness.CreatedTransfers.Should().ContainSingle();
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Finalized);
        (await FloatOf(harness)).Consistent.Should().BeTrue();
    }

    [Fact]
    public async Task A_group_transfer_that_does_not_name_the_cash_out_blocks_the_resolution_and_the_release()
    {
        // L2: membership of the group is the evidence, whatever its metadata says.
        var harness = Funded(500);
        await harness.RequestAsync(amountMinorUnits: 500, tenantId: PricingTenant);
        var payoutId = harness.Payouts.Rows.Single().Id;
        await harness.Service.CollectUnitsAsync(payoutId);
        await harness.Service.ParkForOperatorAsync(payoutId, "operator review");
        harness.ForeignGroupTransfer = true;

        var resolved = await ReturnUnits(harness, payoutId);
        var refused = harness.Payouts.Rows.Single();
        refused.Status = FiatPayoutStatuses.Refused;
        harness.Payouts.Seed(refused);
        harness.Idempotency.Seed(OffRampService.MoveIdempotencyKey(payoutId, "pay"), OffRampService.PayoutOperationType,
            IdempotencyState.Failed, error: "refused");
        var synced = await harness.Funding.SyncAsync(refused);

        resolved.Code.Should().Be(AzoaErrorCodes.Conflict);
        synced.Should().Be(TenantFundingSync.Retry);
        harness.TenantLedger.Debits.Single().State.Should().Be(TenantPayoutDebitStates.Reserved);
    }
}
