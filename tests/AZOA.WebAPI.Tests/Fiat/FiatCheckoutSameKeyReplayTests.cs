// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Fiat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// The same-key resend contract for checkout begin (Providers/Fiat/AGENTS.md §same-key-replay-codes),
/// driven through <see cref="FiatCheckoutController"/>, the real <see cref="FiatRailProvider"/>, the
/// real <see cref="StripeGateway"/> and the in-memory hosted-session store. Only Stripe's HTTP is fake.
/// </summary>
public sealed class FiatCheckoutSameKeyReplayTests
{
    /// <summary>Codes on which clients release the request key; INTENT_NEEDS_REVIEW is final for the request but keeps the key.</summary>
    private static readonly string[] KeyReleasingCodes =
    {
        FiatConsumerErrors.RequestKeyInUseCode, FiatConsumerErrors.IntentFinishedCode,
        FiatConsumerErrors.IntentExpiredCode, FiatConsumerErrors.RequestVoidedCode,
    };

    /// <summary>One row of the contract: what Stripe and the node know when the key is resent, and the answer owed.</summary>
    public sealed record Situation(
        string? StripeStatus,
        string? StripePaymentStatus,
        string? LocalState,
        TimeSpan ResendAfter,
        bool StripeReadFails,
        int ExpectedHttp,
        string? ExpectedCode,
        string? ExpectedStatus = null,
        string? ExpectedSettlement = null,
        bool ExpectPaymentUrl = false,
        ExpireFault Expire = ExpireFault.None,
        int ExpectedExpireExecutions = 0,
        string? ExpectedStripeStatusAfter = null);

    private static readonly TimeSpan WithinLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PastExpiry = TimeSpan.FromMinutes(40);

    private static readonly Dictionary<string, Situation> Contract = new()
    {
        ["page open and inside its lifetime"] = new(null, null, null, WithinLifetime, false,
            200, null, FiatIntentStatuses.AwaitingPayment, FiatSettlementStates.NotStarted, ExpectPaymentUrl: true),
        ["bank debit clearing (AwaitingOutcome), page past expiry"] = new("complete", "unpaid", HostedSessionStates.AwaitingOutcome, PastExpiry, false,
            200, null, FiatIntentStatuses.Processing, FiatSettlementStates.NotStarted),
        ["node parked it as clearing while Stripe still reads open, page past expiry"] = new("open", "unpaid", HostedSessionStates.AwaitingOutcome, PastExpiry, false,
            200, null, FiatIntentStatuses.Processing, FiatSettlementStates.NotStarted),
        ["settlement resuming, page past expiry"] = new("complete", "paid", HostedSessionStates.Resuming, PastExpiry, false,
            200, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived),
        ["card paid at Stripe, webhook not yet processed, page past expiry"] = new("complete", "paid", null, PastExpiry, false,
            200, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived),
        ["card paid at Stripe, page still inside its lifetime"] = new("complete", "paid", null, WithinLifetime, false,
            200, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived),
        ["bank debit submitted at Stripe, webhook not yet processed, page past expiry"] = new("complete", "unpaid", null, PastExpiry, false,
            200, null, FiatIntentStatuses.Processing, FiatSettlementStates.NotStarted),
        ["Stripe re-read fails, page past expiry"] = new(null, null, null, PastExpiry, true,
            503, AzoaErrorCodes.DependencyUnavailable),
        ["Stripe re-read fails, page inside its lifetime"] = new(null, null, null, WithinLifetime, true,
            503, AzoaErrorCodes.DependencyUnavailable),
        ["intent completed: the 409 says it was paid"] = new("complete", "paid", HostedSessionStates.Completed, WithinLifetime, false,
            409, FiatConsumerErrors.IntentFinishedCode, FiatIntentStatuses.Complete, FiatSettlementStates.PaymentReceived),
        ["intent completed but Stripe re-read fails: retry, not a bodiless final code"] = new("complete", "paid", HostedSessionStates.Completed, WithinLifetime, true,
            503, AzoaErrorCodes.DependencyUnavailable),
        ["Stripe expired the unpaid session"] = new("expired", "unpaid", null, PastExpiry, false,
            409, FiatConsumerErrors.IntentExpiredCode, FiatIntentStatuses.AwaitingPayment, FiatSettlementStates.PaymentFailed,
            ExpectedStripeStatusAfter: "expired"),
        // Our ExpiresAt IS Stripe's expires_at: a clock past it proves nothing, so the page is expired AT STRIPE first.
        ["Stripe still reads open past expiry: expired at Stripe, then INTENT_EXPIRED"] = new("open", "unpaid", null, PastExpiry, false,
            409, FiatConsumerErrors.IntentExpiredCode, FiatIntentStatuses.AwaitingPayment, FiatSettlementStates.PaymentFailed,
            ExpectedExpireExecutions: 1, ExpectedStripeStatusAfter: "expired"),
        ["Stripe still reads open past expiry, the expire lands but its answer is lost: a fresh read proves it"] = new("open", "unpaid", null, PastExpiry, false,
            409, FiatConsumerErrors.IntentExpiredCode, FiatIntentStatuses.AwaitingPayment, FiatSettlementStates.PaymentFailed,
            Expire: ExpireFault.ResponseLost, ExpectedExpireExecutions: 1, ExpectedStripeStatusAfter: "expired"),
        ["Stripe still reads open past expiry and the payer (mid-3DS) completes during the expire"] = new("open", "unpaid", null, PastExpiry, false,
            200, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived,
            Expire: ExpireFault.PayerCompletesFirst, ExpectedExpireExecutions: 1, ExpectedStripeStatusAfter: "complete"),
        ["Stripe still reads open past expiry, the expire never reaches Stripe and the re-read still says open"] = new("open", "unpaid", null, PastExpiry, false,
            503, AzoaErrorCodes.DependencyUnavailable, Expire: ExpireFault.Unreachable, ExpectedStripeStatusAfter: "open"),
        ["Stripe still reads open past expiry and a same-key expire is still in flight"] = new("open", "unpaid", null, PastExpiry, false,
            503, AzoaErrorCodes.DependencyUnavailable, Expire: ExpireFault.InFlightConflict, ExpectedStripeStatusAfter: "open"),
    };

    public static TheoryData<string> ContractRows()
    {
        var rows = new TheoryData<string>();
        foreach (var row in Contract.Keys)
            rows.Add(row);
        return rows;
    }

    [Theory]
    [MemberData(nameof(ContractRows))]
    public async Task A_resent_key_gets_the_answer_the_same_key_contract_owes(string row)
    {
        var situation = Contract[row];
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();

        var opened = Unwrap(await harness.StartAsync(subject, "resend-key"));
        opened.Http.Should().Be(200);
        var first = opened.Body.Result!;
        first.PaymentUrl.Should().NotBeNull();

        if (situation.StripeStatus is not null)
            harness.Stripe.Settle(first.IntentId, situation.StripeStatus, situation.StripePaymentStatus!);
        if (situation.LocalState is not null)
            await harness.TransitionAsync(first.IntentId, situation.LocalState);
        harness.Clock.Advance(situation.ResendAfter);
        harness.Stripe.FailNextGet = situation.StripeReadFails;
        harness.Stripe.NextExpireFault = situation.Expire;

        var (http, body) = Unwrap(await harness.StartAsync(subject, "resend-key"));

        http.Should().Be(situation.ExpectedHttp, row);
        body.Code.Should().Be(situation.ExpectedCode, row);
        harness.Stripe.SessionsCreated.Should().Be(1, "a resend never opens a second payment page");
        harness.Stripe.ExpireExecutions.Should().Be(situation.ExpectedExpireExecutions, row);
        if (situation.ExpectedStripeStatusAfter is not null)
            harness.Stripe.StatusOf(first.IntentId).Should().Be(situation.ExpectedStripeStatusAfter, row);
        var voidsExpireKey = FiatCheckoutVoid.ExpireIdempotencyKey(first.IntentId.ToString("N"), harness.Stripe.SessionIdOf(first.IntentId));
        harness.Stripe.ExpireIdempotencyKeys.Should().NotContain(key => key != voidsExpireKey, "resume and void share one expire key");
        if (situation.ExpectedHttp == 503)
        {
            body.Result.Should().BeNull("a failed read must not hand back any settlement state, invented or otherwise");
            return;
        }

        // 200 and the final 409s alike carry the projection, so a client that lost its first answer can tell paid from failed.
        var resumed = body.Result!;
        resumed.Resumed.Should().BeTrue();
        resumed.IntentId.Should().Be(first.IntentId);
        resumed.Status.Should().Be(situation.ExpectedStatus, row);
        resumed.SettlementState.Should().Be(situation.ExpectedSettlement, row);
        if (situation.ExpectPaymentUrl)
            resumed.PaymentUrl.Should().Be(first.PaymentUrl);
        else
            resumed.PaymentUrl.Should().BeNull("a page that can no longer take a payment is never handed back");
        resumed.AmountPaidMinorUnits.Should().Be(
            situation.ExpectedSettlement == FiatSettlementStates.PaymentReceived ? (long?)1_000 : null);
        var read = Unwrap(await harness.Controller(subject).Get(subject, first.IntentId)).Body.Result;
        resumed.Should().BeEquivalentTo(read, "the resend carries the very projection GET returns");
    }

    [Fact]
    public async Task A_fresh_begin_answers_from_the_create_itself_so_a_failing_read_cannot_503_an_opened_page()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        harness.Stripe.FailNextGet = true;

        var (http, body) = Unwrap(await harness.StartAsync(subject, "fresh-key"));

        http.Should().Be(200);
        body.Result!.Resumed.Should().BeFalse();
        body.Result.Status.Should().Be(FiatIntentStatuses.AwaitingPayment);
        body.Result.PaymentUrl.Should().Be(harness.Stripe.UrlOf(body.Result.IntentId));
        harness.Stripe.SessionReads.Should().Be(0, "the create's own answer is the fresh read");
    }

    [Fact]
    public async Task Stripe_idempotency_key_in_use_is_a_non_final_409_and_the_retry_resumes_the_same_page()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        harness.Stripe.NextCreateCollidesWithInFlightRequest = true;

        var collided = await harness.StartAsync(subject, "in-flight-key");

        collided.Result.Should().BeOfType<ConflictObjectResult>("a same-key request still in flight is not a refusal");
        var (http, body) = Unwrap(collided);
        http.Should().Be(StatusCodes.Status409Conflict);
        body.Code.Should().Be(AzoaErrorCodes.Conflict);
        KeyReleasingCodes.Should().NotContain(body.Code, "clients release the key on these codes");

        var retried = Unwrap(await harness.StartAsync(subject, "in-flight-key"));
        retried.Http.Should().Be(200);
        retried.Body.Result!.PaymentUrl.Should().Be(harness.Stripe.UrlOf(retried.Body.Result.IntentId));
        harness.Stripe.SessionsCreated.Should().Be(1, "keeping the key let the retry find the in-flight request's page");
    }

    [Fact]
    public async Task Another_accounts_key_and_an_unreplayable_create_answer_their_own_409_codes()
    {
        var harness = new FiatCheckoutHarness();

        // REQUEST_KEY_IN_USE: only reachable on the rail with an explicit shared admission key; the
        // controller always folds the caller's own subject into the key it builds.
        const string sharedKey = "shared-admission-key";
        (await harness.Rail.BeginCheckoutAsync(harness.Request(Guid.NewGuid(), sharedKey))).IsError.Should().BeFalse();
        var keyInUse = await harness.Rail.BeginCheckoutAsync(harness.Request(Guid.NewGuid(), sharedKey));
        keyInUse.Code.Should().Be(FiatConsumerErrors.RequestKeyInUseCode);
        keyInUse.Message.Should().Be("That request key is already in use.");
        keyInUse.Result.Should().BeNull("another account's intent is never revealed");

        // INTENT_NEEDS_REVIEW: the 23-hour replay window elapsed with no provider session attached.
        var reviewSubject = Guid.NewGuid();
        harness.Stripe.LoseNextCreateResponse = true;
        Unwrap(await harness.StartAsync(reviewSubject, "review-key")).Http.Should().Be(503);
        var claim = (await harness.RowForKeyAsync(reviewSubject, "review-key"))!;
        harness.Clock.Advance(TimeSpan.FromHours(24));
        var (http, body) = Unwrap(await harness.StartAsync(reviewSubject, "review-key"));
        http.Should().Be(409);
        body.Code.Should().Be(FiatConsumerErrors.IntentNeedsReviewCode);
        body.Message.Should().Be("This payment needs a manual check before it can continue.");
        KeyReleasingCodes.Should().NotContain(body.Code, "a Stripe session may still sit behind the claim, so the key is kept");
        body.Result!.IntentId.Should().Be(Guid.ParseExact(claim.Id, "N"), "the client keeps the key and names the intent for review");
        body.Result.Status.Should().Be(FiatIntentStatuses.Starting);
        body.Result.PaymentUrl.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_provider_reread_fails_the_read_instead_of_reporting_not_started()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();

        var opened = await harness.Rail.BeginCheckoutAsync(harness.Request(subject, "reread-key"));
        opened.IsError.Should().BeFalse(opened.Message);

        harness.Stripe.FailNextGet = true;
        var (http, body) = Unwrap(await harness.Controller(subject).Get(subject, opened.Result!.IntentId));

        http.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        body.Result.Should().BeNull("a failed read must not hand back any settlement state, invented or otherwise");
    }

    private static (int Http, AZOAResult<FiatCheckoutIntent> Body) Unwrap(ActionResult<AZOAResult<FiatCheckoutIntent>> result)
        => FiatCheckoutHarness.Unwrap(result);
}
