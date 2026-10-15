// SPDX-License-Identifier: UNLICENSED
// L1.5 — OI-32 and OI-33. Every assertion is taken AT THE COUNTER: not "compensation was
// called" but "the treasury counter never moved" and "the partition got its units back", and
// not "a payload exists" but "the six money fields come back off it".

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class ExchangeCompensationTests
{
    private const string ClientKey = "sponsorship-1";

    // ── OI-33: an accepted-then-dropped group unwinds everything it took ───────

    [Fact]
    public async Task An_accepted_then_dropped_buy_hands_the_partition_back_and_credits_nothing()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Dropped());

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.RailUnavailable);

        harness.Movements.Should().BeEmpty(
            "the project treasury counter must never move for a group that never settled — that "
            + "same balance is what a later sell pays real units out against");
        harness.Draws.Select(draw => draw.Kind).Should().Equal(
            ReserveDrawKind.Issue, ReserveDrawKind.Return);
        harness.Draws[1].OperationKey.Should().Be(
            LedgerKey(harness) + ":compensate",
            "the unwind carries its own replay anchor so a retried compensation cannot double-count");
        harness.Draws[1].Amount.Should().Be(new BigInteger(250));
    }

    [Fact]
    public async Task An_accepted_then_dropped_sell_puts_the_treasury_spend_back_and_re_issues_the_partition()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(
            ratePolicy: new TreasuryRateExchangePolicy(harness.Options, new AdmittingLiquidityPolicy()),
            submitter: OutcomeExchangeGroupSubmitter.Dropped());

        var result = await manager.ExchangeAsync(
            harness.Request(direction: ExchangeDirection.Sell), harness.Caller(ClientKey));

        result.IsError.Should().BeTrue();
        harness.Movements.Select(movement => movement.Kind).Should().Equal(
            ProjectTreasuryMovementKind.Spend, ProjectTreasuryMovementKind.Credit);
        harness.Movements[1].Amount.Should().Be(
            new BigInteger(500), "the project gets back exactly what the unsettled sell took");
        harness.Draws.Select(draw => draw.Kind).Should().Equal(
            ReserveDrawKind.Return, ReserveDrawKind.Issue);
    }

    [Fact]
    public async Task An_accepted_but_unconfirmed_group_neither_credits_nor_unwinds()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Accepted());

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.SettlementPending);
        harness.Movements.Should().BeEmpty(
            "an accepted group has not settled, so the project is not credited yet");
        harness.Draws.Should().ContainSingle(
            "the fate of a group the rail still holds is unknown; unwinding it could undo an "
            + "exchange that settles a round later");
    }

    [Fact]
    public async Task A_confirmed_buy_credits_the_project_exactly_once()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Confirmed());

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        result.IsError.Should().BeFalse(result.Message);
        harness.Movements.Should().ContainSingle()
            .Which.Kind.Should().Be(ProjectTreasuryMovementKind.Credit);
        harness.Movements[0].Amount.Should().Be(new BigInteger(500));
    }

    // ── OI-32: the receipt replays its own money fields ───────────────────────

    [Fact]
    public async Task A_settled_exchange_leaves_a_replay_payload_carrying_every_money_field()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Confirmed());

        await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        var operation = harness.Operations.All.Should().ContainSingle().Which;
        operation.Parameters.Should().ContainKey(
            IdempotencyParameterNames.ResultPayload,
            "without it ReconciliationService completes the claim with a raw transaction hash and "
            + "every money field on the receipt route comes back null");

        var replayed = IdempotencyReplay.DeserializeForReplay<ExchangeReceipt>(
            operation.Parameters[IdempotencyParameterNames.ResultPayload]);

        replayed.Should().NotBeNull();
        replayed!.AmountSpent.Should().Be("500");
        replayed.AmountReceived.Should().Be("250");
        replayed.ProjectCreditAmount.Should().Be("500");
        replayed.Rate.Should().Be("0.02");
        replayed.RateSource.Should().Be("ardanova:treasury-rate");
        replayed.SettlementReference.Should().NotBeNullOrWhiteSpace();
        replayed.State.Should().Be(
            ExchangeReceiptState.Completed,
            "a reconciler only ever replays this payload to complete a confirmed exchange");
    }

    [Fact]
    public async Task An_unconfirmed_exchange_still_leaves_a_payload_a_reconciler_can_replay()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Accepted());

        await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        var operation = harness.Operations.All.Should().ContainSingle().Which;
        var replayed = IdempotencyReplay.DeserializeForReplay<ExchangeReceipt>(
            operation.Parameters[IdempotencyParameterNames.ResultPayload]);

        replayed!.AmountSpent.Should().Be("500");
        replayed.AmountReceived.Should().Be("250");
        replayed.SettlementReference.Should().NotBeNullOrWhiteSpace();
        replayed.ProjectCreditAmount.Should().BeNull(
            "the project's ledger has not moved, so the replayed receipt must not say it has");
    }

    [Fact]
    public async Task A_reconciler_confirmed_exchange_answers_the_receipt_route_with_its_money_fields()
    {
        // OI-32's exact harm: the exchange succeeded, reconciliation settled the claim, and the
        // receipt route answered with AmountSpent, AmountReceived, Rate, RateSource and
        // SettlementReference all null, because the row carried no replay payload to settle with.
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Accepted());
        await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        var operation = harness.Operations.All.Single();
        operation.Status = OperationStatus.Exchanged;
        await harness.Idempotency.CompleteAsync(
            LedgerKey(harness),
            operation.Parameters[IdempotencyParameterNames.ResultPayload],
            CancellationToken.None);

        var receipt = await manager.GetReceiptAsync(new ExchangeReceiptQuery
        {
            ApiKeyId = harness.ApiKeyId,
            CallerUserId = harness.TenantId,
            ClientIdempotencyKey = ClientKey,
        });

        receipt.IsError.Should().BeFalse(receipt.Message);
        receipt.Result!.AmountSpent.Should().Be("500");
        receipt.Result.AmountReceived.Should().Be("250");
        receipt.Result.Rate.Should().Be("0.02");
        receipt.Result.RateSource.Should().Be("ardanova:treasury-rate");
        receipt.Result.SettlementReference.Should().NotBeNullOrWhiteSpace();
        receipt.Result.OperationId.Should().Be(operation.Id);
    }


    // ── The credit state is a control, not a note ─────────────────────────────

    [Fact]
    public async Task A_buy_whose_project_credit_never_landed_is_not_reported_complete()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Accepted());
        await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        // Stand in for reconciliation: chain truth confirmed the group and the outer claim was
        // completed from the payload the row carries.
        var operation = harness.Operations.All.Single();
        operation.Status = OperationStatus.Exchanged;
        operation.CompletedDate = DateTime.UtcNow;
        await harness.Idempotency.CompleteAsync(
            LedgerKey(harness),
            operation.Parameters[IdempotencyParameterNames.ResultPayload],
            CancellationToken.None);

        var receipt = await manager.GetReceiptAsync(new ExchangeReceiptQuery
        {
            ApiKeyId = harness.ApiKeyId,
            CallerUserId = harness.TenantId,
            ClientIdempotencyKey = ClientKey,
        });

        receipt.IsError.Should().BeFalse(receipt.Message);
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be("false");
        receipt.Result!.State.Should().Be(
            ExchangeReceiptState.AwaitingReconciliation,
            "the treasury balance is the availability control a later sell is paid out of, so an "
            + "uncredited buy must never present as finished");
        receipt.Result.ProjectCreditAmount.Should().BeNull();
        receipt.Result.RequiresReconciliation.Should().BeTrue();
    }

    [Fact]
    public async Task A_redelivered_post_answers_what_a_get_answers_for_the_same_exchange()
    {
        // The two read seams disagreed. GetReceiptAsync downgraded an outstanding credit to
        // AwaitingReconciliation; the POST replay deserialized the stored payload verbatim, and
        // that payload is built hard-coded to Completed for the reconciler's benefit. So a
        // redelivered POST answered Completed for the same exchange a GET answered
        // AwaitingReconciliation for — and Completed is the stronger, wronger statement, because
        // the project's credit never landed.
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Accepted());
        await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        var operation = harness.Operations.All.Single();
        operation.Status = OperationStatus.Exchanged;
        operation.CompletedDate = DateTime.UtcNow;
        await harness.Idempotency.CompleteAsync(
            LedgerKey(harness),
            operation.Parameters[IdempotencyParameterNames.ResultPayload],
            CancellationToken.None);

        var replayed = await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));
        var read = await manager.GetReceiptAsync(new ExchangeReceiptQuery
        {
            ApiKeyId = harness.ApiKeyId,
            CallerUserId = harness.TenantId,
            ClientIdempotencyKey = ClientKey,
        });

        replayed.IsError.Should().BeFalse(replayed.Message);
        replayed.Result!.State.Should().Be(
            read.Result!.State,
            "one exchange has one state; two seams answering differently means one of them is lying");
        replayed.Result.State.Should().Be(ExchangeReceiptState.AwaitingReconciliation);
        replayed.Result.IsTerminal.Should().BeFalse();
        replayed.Result.RequiresReconciliation.Should().BeTrue();
        replayed.Result.ProjectCreditAmount.Should().BeNull(
            "the project's ledger never moved, so no credit may be reported on a replay either");

        harness.Operations.All.Should().ContainSingle(
            "a replay re-executes nothing — the counter check that this stayed a read");
    }

    [Fact]
    public async Task A_confirmed_buy_states_on_its_row_that_the_project_ledger_moved()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: OutcomeExchangeGroupSubmitter.Confirmed());

        await manager.ExchangeAsync(harness.Request(), harness.Caller(ClientKey));

        harness.Operations.All.Single()
            .Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be("true");
    }

    private static string LedgerKey(ExchangeTestHarness harness) =>
        $"exchange:{harness.ApiKeyId}:{ClientKey}";
}

/// <summary>A submitter that reports one chosen rail outcome and records what it was handed.</summary>
internal sealed class OutcomeExchangeGroupSubmitter : IExchangeGroupSubmitter
{
    private readonly ExchangeGroupOutcome _outcome;

    private OutcomeExchangeGroupSubmitter(ExchangeGroupOutcome outcome) => _outcome = outcome;

    public static OutcomeExchangeGroupSubmitter Confirmed() =>
        new(ExchangeGroupOutcome.Confirmed);

    public static OutcomeExchangeGroupSubmitter Accepted() =>
        new(ExchangeGroupOutcome.Accepted);

    public static OutcomeExchangeGroupSubmitter Dropped() =>
        new(ExchangeGroupOutcome.DroppedAfterAcceptance);

    public List<ExchangeGroupPlan> Submitted { get; } = new();

    public bool CanSubmit(string chainType) => true;

    public Task<AZOAResult<ExchangeGroupSubmission>> SubmitAsync(
        ExchangeGroupPlan plan, CancellationToken ct = default)
    {
        Submitted.Add(plan);
        var legReferences = plan.Legs
            .Select((_, index) => $"TX{index}{plan.GroupIdentity[..8].ToUpperInvariant()}")
            .ToArray();

        return Task.FromResult(AZOAResult<ExchangeGroupSubmission>.Success(
            new ExchangeGroupSubmission(
                $"GROUP{plan.GroupIdentity[..8]}",
                legReferences,
                _outcome == ExchangeGroupOutcome.Confirmed,
                _outcome),
            _outcome == ExchangeGroupOutcome.DroppedAfterAcceptance
                ? "This exchange did not settle in time and has been reversed. Nothing was moved."
                : "Submitted."));
    }
}

/// <summary>A buy-back policy that admits every sell, so a sell path can be exercised at all.</summary>
internal sealed class AdmittingLiquidityPolicy : IExchangeLiquidityPolicy
{
    public Task<AZOAResult<bool>> EnsureSellAllowedAsync(
        ExchangeRateEvaluation evaluation, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Success(true));
}
