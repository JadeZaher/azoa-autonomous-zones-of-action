// SPDX-License-Identifier: UNLICENSED
// L1.8 -- "charged and not credited" has to be visible to a person.
//
// Both durable records under test were already being written correctly. Neither had a reader.
// OnRampSettlementHandler parks a lot at Refused or Stuck and then answers the provider 202
// ("stop retrying"), and FiatEventDispositions parks an in-flight delivery at Received with the
// same 202 -- so after those writes nothing external will ever bring the payment back, and until
// this item nothing internal looked at either table. That is this codebase's recurring defect
// (RUNBOOK §2) in its purest form: the write is durable, the prose says a person will handle it,
// and the enforcement -- the read -- does not exist.
//
// So these tests are about the READ PATH reaching a person, not about the writes:
//   * a parked lot and a Received delivery each appear on their operator board;
//   * the boards distinguish "a person must act" from "the provider will come back", because a
//     board that cannot tell those apart is noise an operator learns to ignore;
//   * a failed read answers 503 and never an empty 200, since "nobody is owed money" is the one
//     answer that must never be a guess;
//   * the two ledger queries actually ask for the rows the boards claim to show, including the
//     lots stranded at Pending that never reach Refused or Stuck at all;
//   * and both boards stay behind the Operator policy.

using System.Reflection;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Fiat;

public class OperatorVisibilityTests
{
    // ── §lots — the board over on_ramp_lot ───────────────────────────────────

    [Fact]
    public async Task ParkedLot_AppearsOnTheOperatorBoard()
    {
        var lot = Lot(OnRampLotStatuses.Stuck, "No issuance succeeded within 5 attempts.");
        var ledger = LedgerReturning(lot);

        var response = await Controller(ledger).ListNeedingOperator(limit: null, CancellationToken.None);

        var board = OkBoard<OnRampLotView>(response);
        board.Should().ContainSingle().Which.Id.Should().Be(lot.Id);
        board[0].Status.Should().Be(OnRampLotStatuses.Stuck);
        board[0].StatusReason.Should().Be("No issuance succeeded within 5 attempts.");
        board[0].ChargedAndUncredited.Should().BeTrue(
            "the lot exists only because the payment cleared, and nothing was credited");
        board[0].NeedsAPersonToMoveIt.Should().BeTrue(
            "a parked lot never becomes Issued on its own");
    }

    [Fact]
    public async Task StrandedPendingLot_IsCharged_ButNotYetAbandonedByTheProvider()
    {
        // The distinction the board exists to make: this money is owed either way, but a Pending
        // lot may still be brought back by the provider's own retry, and a parked one never will.
        var ledger = LedgerReturning(Lot(OnRampLotStatuses.Pending, "The issuance is still settling."));

        var response = await Controller(ledger).ListNeedingOperator(limit: null, CancellationToken.None);

        var board = OkBoard<OnRampLotView>(response);
        board.Should().ContainSingle();
        board[0].ChargedAndUncredited.Should().BeTrue();
        board[0].NeedsAPersonToMoveIt.Should().BeFalse();
    }

    [Theory]
    [InlineData(OnRampLotStatuses.Issued)]
    [InlineData(OnRampLotStatuses.Reversed)]
    public async Task CreditedOrReversedLot_IsNotReportedAsChargedAndUncredited(string status)
    {
        var ledger = LedgerReturning(Lot(status, statusReason: null));

        var response = await Controller(ledger).ListNeedingOperator(limit: null, CancellationToken.None);

        OkBoard<OnRampLotView>(response)[0].ChargedAndUncredited.Should().BeFalse(
            "Issued means the person holds the units and Reversed means the money went back");
    }

    [Fact]
    public async Task LotBoard_WhenTheReadFaults_Answers503AndNotAnEmptyBoard()
    {
        var ledger = new Mock<IOnRampLotLedger>();
        ledger.Setup(candidate => candidate.ListNeedingOperatorAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<OnRampLot>>.Failure("surreal unavailable"));

        var response = await Controller(ledger).ListNeedingOperator(limit: null, CancellationToken.None);

        response.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable,
                "an empty board would tell an operator that nobody is owed money");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("../../etc/passwd")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task GetByIntent_RejectsAnIdItCannotParse_WithoutTouchingTheLedger(string intentId)
    {
        var ledger = new Mock<IOnRampLotLedger>();

        var response = await Controller(ledger).GetByIntent(intentId, CancellationToken.None);

        response.Should().BeOfType<BadRequestObjectResult>();
        ledger.Verify(
            candidate => candidate.GetByIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetByIntent_NormalisesADashedIdToTheCompactFormTheLedgerAnchorsOn()
    {
        var intentId = Guid.NewGuid();
        var ledger = new Mock<IOnRampLotLedger>();
        ledger.Setup(candidate => candidate.GetByIntentAsync(
                intentId.ToString("N"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<OnRampLot?> { Result = Lot(OnRampLotStatuses.Refused, "wrong currency") });

        var response = await Controller(ledger).GetByIntent(intentId.ToString("D"), CancellationToken.None);

        response.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<OnRampLotView>>()
            .Which.Result!.Status.Should().Be(OnRampLotStatuses.Refused);
    }

    // ── §lot-query — the board asks for the rows it claims to show ───────────

    [Fact]
    public async Task LotLedger_AsksForRefusedAndStuck_AndForLotsStrandedAtPending()
    {
        var issued = new List<SurrealQuery>();
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QueryAsync<OnRampLot>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .Callback<SurrealQuery, CancellationToken>((query, _) => issued.Add(query));

        var result = await new SurrealOnRampLotLedger(executor.Object)
            .ListNeedingOperatorAsync(50, CancellationToken.None);

        result.IsError.Should().BeFalse();
        issued.Should().HaveCount(2, "the decided lots and the stranded ones are two reads");
        issued[0].Sql.Should().Contain("FROM on_ramp_lot").And.Contain("status INSIDE $_statuses");
        // Every failed attempt leaves the lot Pending, so a lot the provider gave up on never
        // reaches Refused or Stuck. Listing only those would hide the commonest stranded charge.
        issued[1].Sql.Should().Contain("status = $_status").And.Contain("updated_at < $_before");
        issued.Should().OnlyContain(query => query.Sql.Contains("LIMIT $_limit"));
    }

    [Fact]
    public void MergeOperatorReads_ShowsEachLotOnce_NewestFirst()
    {
        // The two reads are not one transaction: a lot parked between them satisfies both.
        var newest = Lot(OnRampLotStatuses.Refused, "wrong currency", id: "newest");
        newest.CreatedAt = DateTimeOffset.UtcNow;
        var oldest = Lot(OnRampLotStatuses.Pending, "still settling", id: "oldest");
        oldest.CreatedAt = DateTimeOffset.UtcNow.AddDays(-9);

        var merged = SurrealOnRampLotLedger.MergeOperatorReads(
            new[] { newest, oldest }, new[] { oldest }, limit: 10);

        merged.Select(row => row.Id).Should().Equal("newest", "oldest");
    }

    // ── §events — the board over fiat_event ─────────────────────────────────

    [Fact]
    public async Task ReceivedEvent_AppearsOnTheOperatorBoard()
    {
        // A delivery settled as AcceptedPending: the row stays Received and the provider is told
        // to stop. OI-65 is that nothing read this back.
        var row = Event(FiatEventStatuses.Received, attemptCount: 1);
        var inbox = InboxReturning(row);

        var response = await EventController(inbox)
            .ListNeedingOperator(limit: null, CancellationToken.None);

        var board = OkBoard<FiatEventView>(response);
        board.Should().ContainSingle().Which.Id.Should().Be(row.Id);
        board[0].Status.Should().Be(FiatEventStatuses.Received);
        board[0].ProviderWasToldToStopRetrying.Should().BeTrue(
            "AcceptedPending answers 202, so no redelivery is coming for this one");
    }

    [Fact]
    public async Task AdmittedButUnsettledEvent_IsNotReportedAsAbandoned()
    {
        // Received with no attempts is a delivery that never reached the outcome write. The
        // provider's own retry still owns it, and saying otherwise would send an operator to
        // reconcile a payment that is about to reconcile itself.
        var inbox = InboxReturning(Event(FiatEventStatuses.Received, attemptCount: 0));

        var response = await EventController(inbox)
            .ListNeedingOperator(limit: null, CancellationToken.None);

        OkBoard<FiatEventView>(response)[0].ProviderWasToldToStopRetrying.Should().BeFalse();
    }

    [Fact]
    public async Task FailedEvent_AppearsOnTheBoard_ButIsLeftToTheProvidersRetry()
    {
        var inbox = InboxReturning(Event(FiatEventStatuses.Failed, attemptCount: 3));

        var response = await EventController(inbox)
            .ListNeedingOperator(limit: null, CancellationToken.None);

        var board = OkBoard<FiatEventView>(response);
        board.Should().ContainSingle();
        board[0].ProviderWasToldToStopRetrying.Should().BeFalse(
            "a Failed delivery was answered 500, which is the provider's cue to retry");
    }

    [Fact]
    public async Task EventBoard_WhenTheReadFaults_Answers503AndNotAnEmptyBoard()
    {
        var inbox = new Mock<IStripeWebhookInbox>();
        inbox.Setup(candidate => candidate.ListNeedingOperatorAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<FiatEvent>>.Failure("surreal unavailable"));

        var response = await EventController(inbox)
            .ListNeedingOperator(limit: null, CancellationToken.None);

        response.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task EventInbox_AsksForTheDeliveriesNoHandlerFinished()
    {
        var issued = new List<SurrealQuery>();
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QueryAsync<FiatEvent>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .Callback<SurrealQuery, CancellationToken>((query, _) => issued.Add(query));

        var result = await new StripeWebhookInbox(executor.Object)
            .ListNeedingOperatorAsync(50, CancellationToken.None);

        result.IsError.Should().BeFalse();
        issued.Should().ContainSingle();
        issued[0].Sql.Should().Contain("FROM fiat_event")
            .And.Contain("status INSIDE $_statuses")
            .And.Contain("ORDER BY received_at DESC");
    }

    [Theory]
    [InlineData(OnRampLotStatuses.Refused, true)]
    [InlineData(OnRampLotStatuses.Stuck, true)]
    [InlineData(OnRampLotStatuses.Pending, false)]
    [InlineData(OnRampLotStatuses.Issued, false)]
    [InlineData(OnRampLotStatuses.Reversed, false)]
    public void LotNeedsOperator_AgreesWithTheStatusesTheOperatorReadBinds(string status, bool expected)
    {
        // The settlement handler's stop-retrying short-circuit and the operator read must not
        // drift apart: one predicate, one list, both built from the same constants.
        OnRampLotStatuses.NeedsOperator(status).Should().Be(expected);
        OnRampLotStatuses.NeedingOperatorStatuses.Contains(status).Should().Be(expected);
    }

    // ── §policy — both boards read other people's payments ──────────────────

    [Theory]
    [InlineData(typeof(OnRampLotOperatorController))]
    [InlineData(typeof(FiatEventOperatorController))]
    public void OperatorBoards_AreBehindTheOperatorPolicy(Type controller)
        => controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .Should().Contain("Operator");

    [Theory]
    [InlineData(typeof(OnRampLotOperatorController))]
    [InlineData(typeof(FiatEventOperatorController))]
    public void OperatorBoards_ExposeNoAnonymousRoute(Type controller)
        => controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes(typeof(AllowAnonymousAttribute), true).Length > 0)
            .Should().BeEmpty("these routes project other people's payment records");

    // ── fixtures ────────────────────────────────────────────────────────────

    private static OnRampLotOperatorController Controller(Mock<IOnRampLotLedger> ledger)
        => new(ledger.Object, NullLogger<OnRampLotOperatorController>.Instance);

    private static FiatEventOperatorController EventController(Mock<IStripeWebhookInbox> inbox)
        => new(inbox.Object, NullLogger<FiatEventOperatorController>.Instance);

    private static Mock<IOnRampLotLedger> LedgerReturning(params OnRampLot[] rows)
    {
        var ledger = new Mock<IOnRampLotLedger>();
        ledger.Setup(candidate => candidate.ListNeedingOperatorAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<OnRampLot>>.Success(rows.ToList()));
        return ledger;
    }

    private static Mock<IStripeWebhookInbox> InboxReturning(params FiatEvent[] rows)
    {
        var inbox = new Mock<IStripeWebhookInbox>();
        inbox.Setup(candidate => candidate.ListNeedingOperatorAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<FiatEvent>>.Success(rows.ToList()));
        return inbox;
    }

    private static OnRampLot Lot(string status, string? statusReason, string id = "lot-1") => new()
    {
        Id = id,
        IntentId = Guid.NewGuid().ToString("N"),
        SubjectUserId = Guid.NewGuid().ToString("N"),
        ChainType = "Algorand",
        AssetId = "arda",
        Status = status,
        StatusReason = statusReason,
        FiatMinorUnits = 2500,
        CurrencyCode = "USD",
        QuantityBaseUnits = "250000",
        GrossQuantityBaseUnits = "250000",
        AttemptCount = 2,
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-4),
        UpdatedAt = DateTimeOffset.UtcNow.AddHours(-4),
    };

    private static FiatEvent Event(string status, long attemptCount) => new()
    {
        Id = "event-1",
        Provider = "stripe",
        ProviderEventId = "evt_1",
        EventType = "checkout.session.completed",
        Environment = "test",
        Status = status,
        AttemptCount = attemptCount,
        PayloadDigest = "digest",
        ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
    };

    private static IReadOnlyList<TView> OkBoard<TView>(IActionResult response)
        => response.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<IReadOnlyList<TView>>>()
            .Which.Result!;
}
