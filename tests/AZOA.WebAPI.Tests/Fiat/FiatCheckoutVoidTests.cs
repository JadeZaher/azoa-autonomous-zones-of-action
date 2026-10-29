// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// The request-key void contract (Providers/Fiat/AGENTS.md §request-void), driven through
/// <c>POST /api/fiat/checkout/{userId}/requests/{requestKey}/void</c>, the real rail and gateway, and
/// the in-memory store. Only Stripe's HTTP is fake. "Voided" must mean nothing was or can be paid.
/// </summary>
public sealed class FiatCheckoutVoidTests
{
    private static readonly TimeSpan WithinLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PastExpiry = TimeSpan.FromMinutes(40);

    [Fact]
    public async Task Voiding_a_key_nothing_was_admitted_under_answers_voided_and_a_later_begin_gets_REQUEST_VOIDED()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();

        var (voidHttp, voided) = Unwrap(await harness.VoidAsync(subject, "never-sent"));

        voidHttp.Should().Be(200);
        voided.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        voided.Result.IntentId.Should().BeNull("no intent was ever admitted under this key");
        voided.Result.Intent.Should().BeNull();

        var begin = await harness.StartAsync(subject, "never-sent");
        begin.Result.Should().BeOfType<ConflictObjectResult>("a voided key is a 409, never a 400");
        var (http, body) = Unwrap(begin);
        http.Should().Be(409);
        body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
        body.Result.Should().BeNull();
        harness.Stripe.SessionsCreated.Should().Be(0, "a voided key never reaches Stripe");
    }

    [Fact]
    public async Task Voiding_an_admitted_key_with_no_page_attached_abandons_it_so_the_lost_create_is_never_replayed()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        harness.Stripe.LoseNextCreateResponse = true;
        Unwrap(await harness.StartAsync(subject, "lost-create")).Http.Should().Be(503);
        var claim = (await harness.RowForKeyAsync(subject, "lost-create"))!;
        claim.ProviderSessionId.Should().BeNull("the create's answer was lost, so no page was ever attached or handed out");

        var (http, body) = Unwrap(await harness.VoidAsync(subject, "lost-create"));

        http.Should().Be(200);
        body.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        body.Result.IntentId.Should().Be(Guid.ParseExact(claim.Id, "N"));
        var resend = Unwrap(await harness.StartAsync(subject, "lost-create"));
        resend.Http.Should().Be(409);
        resend.Body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
        harness.Stripe.SessionsCreated.Should().Be(1, "the lost create is not replayed, so its page is never attached");
        (await harness.RowAsync(body.Result.IntentId!.Value)).ProviderSessionId.Should().BeNull();
    }

    /// <summary>A page was attached and handed out; what Stripe and the node know when the void arrives, and the answer owed.</summary>
    public sealed record AttachedSituation(
        string? StripeStatus,
        string? StripePaymentStatus,
        string? LocalState,
        TimeSpan VoidAfter,
        bool StripeReadFails,
        ExpireFault Fault,
        int ExpectedHttp,
        string? ExpectedOutcome,
        string? ExpectedCode = null,
        string? ExpectedIntentStatus = null,
        string? ExpectedSettlement = null,
        int ExpectedExpireExecutions = 0,
        string ExpectedStripeStatusAfter = "open");

    private static readonly Dictionary<string, AttachedSituation> AttachedContract = new()
    {
        ["page open: expired at Stripe, then abandoned"] = new(null, null, null, WithinLifetime, false, ExpireFault.None,
            200, FiatVoidOutcomes.Voided, ExpectedExpireExecutions: 1, ExpectedStripeStatusAfter: "expired"),
        ["page past expiry while Stripe still reads open: expired at Stripe, then abandoned"] = new("open", "unpaid", null, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Voided, ExpectedExpireExecutions: 1, ExpectedStripeStatusAfter: "expired"),
        ["Stripe already expired the unpaid page: abandoned without an expire call"] = new("expired", "unpaid", null, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Voided, ExpectedStripeStatusAfter: "expired"),
        ["expire landed but its answer was lost: a fresh read proves it"] = new(null, null, null, WithinLifetime, false, ExpireFault.ResponseLost,
            200, FiatVoidOutcomes.Voided, ExpectedExpireExecutions: 1, ExpectedStripeStatusAfter: "expired"),
        ["the payer paid while the expire was in flight"] = new(null, null, null, WithinLifetime, false, ExpireFault.PayerCompletesFirst,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived,
            ExpectedExpireExecutions: 1, ExpectedStripeStatusAfter: "complete"),
        ["card paid at Stripe, webhook not yet processed"] = new("complete", "paid", null, WithinLifetime, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived, ExpectedStripeStatusAfter: "complete"),
        ["bank debit submitted at Stripe, webhook not yet processed"] = new("complete", "unpaid", null, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Processing, FiatSettlementStates.NotStarted, ExpectedStripeStatusAfter: "complete"),
        ["the node parked it as clearing while Stripe still reads open"] = new("open", "unpaid", HostedSessionStates.AwaitingOutcome, WithinLifetime, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Processing, FiatSettlementStates.NotStarted),
        ["bank debit clearing (AwaitingOutcome)"] = new("complete", "unpaid", HostedSessionStates.AwaitingOutcome, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Processing, FiatSettlementStates.NotStarted, ExpectedStripeStatusAfter: "complete"),
        ["settlement resuming"] = new("complete", "paid", HostedSessionStates.Resuming, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived, ExpectedStripeStatusAfter: "complete"),
        ["settled"] = new("complete", "paid", HostedSessionStates.Completed, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Complete, FiatSettlementStates.PaymentReceived, ExpectedStripeStatusAfter: "complete"),
        ["payment failed"] = new("complete", "unpaid", HostedSessionStates.Failed, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Failed, FiatSettlementStates.NotStarted, ExpectedStripeStatusAfter: "complete"),
        ["page expired and recorded by the webhook"] = new("expired", "unpaid", HostedSessionStates.Abandoned, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Expired, FiatSettlementStates.PaymentFailed, ExpectedStripeStatusAfter: "expired"),
        // LOW-3: "expired" would read as settled-and-unpaid; a fresh Stripe "complete" outranks the row.
        ["row recorded expired but a fresh Stripe read says the payer completed it"] = new("complete", "paid", HostedSessionStates.Abandoned, PastExpiry, false, ExpireFault.None,
            200, FiatVoidOutcomes.Revealed, null, FiatIntentStatuses.Processing, FiatSettlementStates.PaymentReceived, ExpectedStripeStatusAfter: "complete"),
        ["Stripe cannot be read"] = new(null, null, null, WithinLifetime, true, ExpireFault.None,
            503, null, AzoaErrorCodes.DependencyUnavailable),
        ["expire never reached Stripe and Stripe still reads open"] = new(null, null, null, WithinLifetime, false, ExpireFault.Unreachable,
            503, null, AzoaErrorCodes.DependencyUnavailable),
        ["a same-key expire is still in flight at Stripe"] = new(null, null, null, WithinLifetime, false, ExpireFault.InFlightConflict,
            503, null, AzoaErrorCodes.DependencyUnavailable),
    };

    public static TheoryData<string> AttachedRows()
    {
        var rows = new TheoryData<string>();
        foreach (var row in AttachedContract.Keys)
            rows.Add(row);
        return rows;
    }

    [Theory]
    [MemberData(nameof(AttachedRows))]
    public async Task Voiding_a_key_whose_page_was_handed_out_voids_only_what_provably_cannot_be_paid(string row)
    {
        var situation = AttachedContract[row];
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        var first = Unwrap(await harness.StartAsync(subject, "void-key")).Body.Result!;
        first.PaymentUrl.Should().NotBeNull();
        if (situation.StripeStatus is not null)
            harness.Stripe.Settle(first.IntentId, situation.StripeStatus, situation.StripePaymentStatus!);
        if (situation.LocalState is not null)
            await harness.TransitionAsync(first.IntentId, situation.LocalState);
        var before = await harness.RowAsync(first.IntentId);
        harness.Clock.Advance(situation.VoidAfter);
        harness.Stripe.FailNextGet = situation.StripeReadFails;
        harness.Stripe.NextExpireFault = situation.Fault;

        var (http, body) = Unwrap(await harness.VoidAsync(subject, "void-key"));

        http.Should().Be(situation.ExpectedHttp, row);
        body.Code.Should().Be(situation.ExpectedCode, row);
        harness.Stripe.ExpireExecutions.Should().Be(situation.ExpectedExpireExecutions, row);
        harness.Stripe.StatusOf(first.IntentId).Should().Be(situation.ExpectedStripeStatusAfter, row);
        harness.Stripe.SessionsCreated.Should().Be(1, "a void never opens a page");
        var after = await harness.RowAsync(first.IntentId);
        var resend = Unwrap(await harness.StartAsync(subject, "void-key"));

        if (situation.ExpectedOutcome == FiatVoidOutcomes.Voided)
        {
            body.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);
            body.Result.IntentId.Should().Be(first.IntentId);
            body.Result.Intent.Should().BeNull();
            FiatCheckoutVoid.IsVoided(after).Should().BeTrue(row);
            resend.Http.Should().Be(409);
            resend.Body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
            return;
        }

        // Revealed or unconfirmed: the intent is untouched and the key still names it.
        after.State.Should().Be(before.State, row);
        after.StateVersion.Should().Be(before.StateVersion, row);
        resend.Body.Code.Should().NotBe(FiatConsumerErrors.RequestVoidedCode, row);
        if (situation.ExpectedHttp != 200)
        {
            body.Result.Should().BeNull();
            return;
        }

        body.Result!.Outcome.Should().Be(FiatVoidOutcomes.Revealed, row);
        body.Result.IntentId.Should().Be(first.IntentId);
        var intent = body.Result.Intent!;
        intent.IntentId.Should().Be(first.IntentId);
        intent.Status.Should().Be(situation.ExpectedIntentStatus, row);
        intent.SettlementState.Should().Be(situation.ExpectedSettlement, row);
        intent.PaymentUrl.Should().BeNull("a page that can no longer take a payment is never handed back");
        intent.AmountPaidMinorUnits.Should().Be(
            situation.ExpectedSettlement == FiatSettlementStates.PaymentReceived ? (long?)1_000 : null);
        var read = Unwrap(await harness.Controller(subject).Get(subject, first.IntentId)).Body.Result;
        intent.Should().BeEquivalentTo(read, "revealed carries the very projection GET returns");
    }

    /// <summary>Why no begin may replay an unattached claim's lost create any more.</summary>
    public enum UnreplayableCause
    {
        /// <summary>The creation fence began more than 23 hours ago; Stripe may have pruned its idempotency key.</summary>
        PastReplayWindow,

        /// <summary>An operator quarantined the claim because its create may have reached Stripe.</summary>
        Quarantined,
    }

    [Theory]
    [InlineData(UnreplayableCause.PastReplayWindow)]
    [InlineData(UnreplayableCause.Quarantined)]
    public async Task An_unattached_claim_no_begin_can_replay_is_voided_and_a_later_begin_gets_REQUEST_VOIDED(UnreplayableCause cause)
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        harness.Stripe.LoseNextCreateResponse = true;
        Unwrap(await harness.StartAsync(subject, "stale-key")).Http.Should().Be(503);
        await MakeUnreplayableAsync(harness, (await harness.RowForKeyAsync(subject, "stale-key"))!, cause);
        var stuck = Unwrap(await harness.StartAsync(subject, "stale-key"));
        stuck.Body.Code.Should().Be(FiatConsumerErrors.IntentNeedsReviewCode, "begin still refuses to resume a create it cannot prove");

        var (http, body) = Unwrap(await harness.VoidAsync(subject, "stale-key"));

        http.Should().Be(200, "the void is the key holder's only exit; a 409 here traps the key forever");
        body.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        var intentId = Guid.ParseExact((await harness.RowForKeyAsync(subject, "stale-key"))!.Id, "N");
        body.Result.IntentId.Should().Be(intentId);
        var row = await harness.RowAsync(intentId);
        FiatCheckoutVoid.IsVoided(row).Should().BeTrue();
        row.ProviderSessionId.Should().BeNull("nothing was ever attached, so no page URL ever left the node");
        var resend = Unwrap(await harness.StartAsync(subject, "stale-key"));
        resend.Http.Should().Be(409);
        resend.Body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
        resend.Body.Result.Should().BeNull();
        harness.Stripe.SessionsCreated.Should().Be(1, "the lost create is never replayed");
    }

    public static TheoryData<UnreplayableCause, bool> LateAttachRaces() => new()
    {
        { UnreplayableCause.PastReplayWindow, false },
        { UnreplayableCause.PastReplayWindow, true },
        { UnreplayableCause.Quarantined, false },
        { UnreplayableCause.Quarantined, true },
    };

    [Theory]
    [MemberData(nameof(LateAttachRaces))]
    public async Task A_late_attach_that_beats_the_void_on_an_unreplayable_claim_makes_the_void_re_decide_on_the_handed_out_page(
        UnreplayableCause cause, bool payerPaysDuringTheExpire)
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        var voidReachedItsCas = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVoid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ActionResult<AZOAResult<FiatCheckoutVoidResult>>>? voiding = null;
        harness.Store.BeforeTransition = async () =>
        {
            voidReachedItsCas.SetResult();
            await releaseVoid.Task;
        };
        harness.Store.BeforeAttach = async () =>
        {
            // The create is in flight when the claim turns unreplayable; the void reads it unattached
            // and stops at its abandon CAS, so the late original response attaches first.
            await MakeUnreplayableAsync(harness, (await harness.RowForKeyAsync(subject, "late-key"))!, cause);
            voiding = Task.Run(() => harness.VoidAsync(subject, "late-key"));
            // A void that answers without trying its CAS must fail the test, not hang it.
            await Task.WhenAny(voidReachedItsCas.Task, voiding);
        };

        var begin = Unwrap(await harness.StartAsync(subject, "late-key"));
        harness.Stripe.NextExpireFault = payerPaysDuringTheExpire ? ExpireFault.PayerCompletesFirst : ExpireFault.None;
        releaseVoid.SetResult();
        var (voidHttp, voidBody) = Unwrap(await voiding!);

        begin.Http.Should().Be(200, "the late original response won the attach");
        var opened = begin.Body.Result!;
        opened.PaymentUrl.Should().NotBeNull("the page was handed out");
        voidHttp.Should().Be(200);
        voidBody.Result!.IntentId.Should().Be(opened.IntentId);
        harness.Stripe.ExpireExecutions.Should().Be(1, "the void lost its CAS, re-read the attached row and expired its page at Stripe");
        var row = await harness.RowAsync(opened.IntentId);
        if (payerPaysDuringTheExpire)
        {
            voidBody.Result.Outcome.Should().Be(FiatVoidOutcomes.Revealed, "a page that took a payment is never voided");
            voidBody.Result.Intent!.Status.Should().Be(FiatIntentStatuses.Processing);
            voidBody.Result.Intent.SettlementState.Should().Be(FiatSettlementStates.PaymentReceived);
            FiatCheckoutVoid.IsVoided(row).Should().BeFalse();
            return;
        }

        voidBody.Result.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        harness.Stripe.StatusOf(opened.IntentId).Should().Be("expired", "voided only once the handed-out page can no longer be paid");
        FiatCheckoutVoid.IsVoided(row).Should().BeTrue();
        Unwrap(await harness.StartAsync(subject, "late-key")).Body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
    }

    /// <summary>Turns an unattached claim into one <see cref="FiatHostedCreation.IsUnreplayable"/> refuses.</summary>
    private static async Task MakeUnreplayableAsync(FiatCheckoutHarness harness, HostedSession claim, UnreplayableCause cause)
    {
        if (cause == UnreplayableCause.PastReplayWindow)
        {
            harness.Clock.Advance(FiatHostedCreation.ReplayWindow + TimeSpan.FromHours(1));
            return;
        }

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        var quarantined = await harness.Store.ReconcileAdmissionAsync(
            claim, Guid.NewGuid(), "create may have reached Stripe", providerSession: null, harness.Clock.GetUtcNow());
        quarantined.Result!.CreationQuarantinedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Voiding_twice_gives_the_same_answer_and_expires_the_page_at_Stripe_once()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        var first = Unwrap(await harness.StartAsync(subject, "twice-key")).Body.Result!;

        var once = Unwrap(await harness.VoidAsync(subject, "twice-key"));
        var twice = Unwrap(await harness.VoidAsync(subject, "twice-key"));
        var tombstoneOnce = Unwrap(await harness.VoidAsync(subject, "never-sent"));
        var tombstoneTwice = Unwrap(await harness.VoidAsync(subject, "never-sent"));

        twice.Http.Should().Be(200);
        twice.Body.Result.Should().BeEquivalentTo(once.Body.Result);
        twice.Body.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        twice.Body.Result.IntentId.Should().Be(first.IntentId);
        tombstoneTwice.Http.Should().Be(200);
        tombstoneTwice.Body.Result.Should().BeEquivalentTo(tombstoneOnce.Body.Result);
        tombstoneTwice.Body.Result!.IntentId.Should().BeNull();
        harness.Stripe.ExpireIdempotencyKeys.Should().HaveCount(1, "the second void reads the voided row and never calls Stripe");
    }

    public static TheoryData<ExpireFault> ExpireFaults() => new()
    {
        ExpireFault.None, ExpireFault.Unreachable, ExpireFault.InFlightConflict, ExpireFault.ResponseLost,
    };

    [Theory]
    [MemberData(nameof(ExpireFaults))]
    public async Task Retried_voids_send_one_intent_derived_expire_key_so_Stripe_expires_at_most_once(ExpireFault fault)
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        var first = Unwrap(await harness.StartAsync(subject, "retry-key")).Body.Result!;
        harness.Stripe.NextExpireFault = fault;

        var answers = new List<int>();
        for (var attempt = 0; attempt < 3 && (answers.Count == 0 || answers[^1] != 200); attempt++)
            answers.Add(Unwrap(await harness.VoidAsync(subject, "retry-key")).Http);
        var last = Unwrap(await harness.VoidAsync(subject, "retry-key"));

        answers.Should().NotContain(code => code != 200 && code != 503, "a failed attempt is retryable, never final");
        last.Http.Should().Be(200);
        last.Body.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        harness.Stripe.ExpireExecutions.Should().Be(1, "Stripe executes the expire once however often the void is retried");
        harness.Stripe.ExpireIdempotencyKeys.Distinct().Should().ContainSingle().Which.Should().Be(
            FiatCheckoutVoid.ExpireIdempotencyKey(first.IntentId.ToString("N"), harness.Stripe.SessionIdOf(first.IntentId)));
    }

    [Fact]
    public async Task A_void_that_lands_between_begins_claim_and_its_creation_fence_wins_and_begin_gets_REQUEST_VOIDED()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        ActionResult<AZOAResult<FiatCheckoutVoidResult>>? voidAnswer = null;
        harness.Store.BeforeBeginCreation = async () => voidAnswer = await harness.VoidAsync(subject, "race-key");

        var (http, body) = Unwrap(await harness.StartAsync(subject, "race-key"));

        http.Should().Be(409);
        body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
        harness.Stripe.SessionsCreated.Should().Be(0, "the begin lost the fence, so it never called Stripe");
        var voided = Unwrap(voidAnswer!).Body.Result!;
        voided.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        voided.IntentId.Should().Be(Guid.ParseExact((await harness.RowForKeyAsync(subject, "race-key"))!.Id, "N"));
    }

    [Fact]
    public async Task A_void_that_lands_while_begins_create_is_in_flight_wins_the_attach_and_the_unhanded_page_is_expired()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        ActionResult<AZOAResult<FiatCheckoutVoidResult>>? voidAnswer = null;
        harness.Store.BeforeAttach = async () => voidAnswer = await harness.VoidAsync(subject, "attach-race-key");

        var (http, body) = Unwrap(await harness.StartAsync(subject, "attach-race-key"));

        http.Should().Be(409);
        body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
        body.Result.Should().BeNull("the page URL never leaves the node once the void has won");
        var voided = Unwrap(voidAnswer!).Body.Result!;
        voided.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        var row = await harness.RowAsync(voided.IntentId!.Value);
        row.ProviderSessionId.Should().BeNull("the attach lost to the void");
        harness.Stripe.SessionsCreated.Should().Be(1);
        harness.Stripe.StatusOf(voided.IntentId.Value).Should().Be("expired", "begin closes the page nobody was handed");
    }

    [Fact]
    public async Task A_begin_that_attaches_while_a_void_is_deciding_makes_the_void_re_decide_and_expire_the_handed_out_page()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        var voidReachedItsCas = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVoid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ActionResult<AZOAResult<FiatCheckoutVoidResult>>>? voiding = null;
        harness.Store.BeforeTransition = async () =>
        {
            voidReachedItsCas.SetResult();
            await releaseVoid.Task;
        };
        harness.Store.BeforeAttach = async () =>
        {
            // The void reads the unattached claim and stops just before its abandon CAS; the attach goes first.
            voiding = Task.Run(() => harness.VoidAsync(subject, "decide-race-key"));
            await voidReachedItsCas.Task;
        };

        var begin = Unwrap(await harness.StartAsync(subject, "decide-race-key"));
        releaseVoid.SetResult();
        var (voidHttp, voidBody) = Unwrap(await voiding!);

        begin.Http.Should().Be(200, "the attach won the version race");
        var opened = begin.Body.Result!;
        opened.PaymentUrl.Should().NotBeNull();
        voidHttp.Should().Be(200);
        voidBody.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);
        voidBody.Result.IntentId.Should().Be(opened.IntentId);
        harness.Stripe.StatusOf(opened.IntentId).Should().Be("expired", "the void lost its CAS, re-read the attached row and closed the page");
        harness.Stripe.ExpireExecutions.Should().Be(1);
        Unwrap(await harness.StartAsync(subject, "decide-race-key")).Body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
    }

    [Fact]
    public async Task Concurrent_begins_and_voids_never_leave_a_payable_page_behind_a_voided_answer()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();

        for (var round = 0; round < 40; round++)
        {
            var key = $"stress-{round}";
            var begin = Task.Run(() => harness.StartAsync(subject, key));
            var voiding = Task.Run(() => harness.VoidAsync(subject, key));
            await Task.WhenAll(begin, voiding);

            var (voidHttp, voidBody) = Unwrap(voiding.Result);
            var (beginHttp, beginBody) = Unwrap(begin.Result);
            beginHttp.Should().BeOneOf(200, 409, 503);
            if (voidHttp != 200)
            {
                voidHttp.Should().Be(503, $"round {round}: a contended void is retryable");
                continue;
            }

            voidBody.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided, $"round {round}: nothing was paid, so nothing is revealed");
            if (beginHttp == 409)
                beginBody.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode, $"round {round}");
            var row = (await harness.RowForKeyAsync(subject, key))!;
            harness.Stripe.StatusesFor(row.Id).Should().NotContain("open", $"round {round}: a voided key leaves no payable page");
            Unwrap(await harness.StartAsync(subject, key)).Body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode, $"round {round}");
        }
    }

    [Fact]
    public async Task A_first_party_caller_cannot_void_another_accounts_key()
    {
        var harness = new FiatCheckoutHarness();
        var victim = Guid.NewGuid();

        var (http, body) = Unwrap(await harness.VoidAsync(victim, "victim-key", caller: Guid.NewGuid()));

        http.Should().Be(403);
        body.Code.Should().Be(AzoaErrorCodes.Forbidden);
        (await harness.RowForKeyAsync(victim, "victim-key")).Should().BeNull("a refused void writes no tombstone");
        var begin = Unwrap(await harness.StartAsync(victim, "victim-key"));
        begin.Http.Should().Be(200);
        begin.Body.Result!.PaymentUrl.Should().NotBeNull();
    }

    [Fact]
    public async Task A_key_routing_cannot_spell_is_refused_rather_than_voiding_a_different_key()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        var opened = Unwrap(await harness.StartAsync(subject, "a/b")).Body.Result!;

        // ASP.NET routing hands the action an encoded '/' as the literal "%2F".
        var (http, body) = Unwrap(await harness.VoidAsync(subject, "a%2Fb"));

        http.Should().Be(400);
        body.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
        (await harness.RowForKeyAsync(subject, "a%2Fb")).Should().BeNull("no tombstone may be written under a key nobody used");
        harness.Stripe.StatusOf(opened.IntentId).Should().Be("open");
        var resend = Unwrap(await harness.StartAsync(subject, "a/b"));
        resend.Http.Should().Be(200);
        resend.Body.Result!.PaymentUrl.Should().Be(opened.PaymentUrl);
    }

    [Fact]
    public async Task A_tenant_top_up_on_a_voided_key_is_a_409_REQUEST_VOIDED()
    {
        var harness = new FiatCheckoutHarness();
        var tenant = Guid.NewGuid();
        var voided = await harness.Rail.VoidRequestAsync(tenant, null, TenantPayoutTopUp.AdmissionKeyFor(tenant, "float-key"));
        voided.Result!.Outcome.Should().Be(FiatVoidOutcomes.Voided);

        var topUp = await harness.TopUpController(tenant).TopUp(
            new TenantPayoutTopUpRequest { RequestKey = "float-key", AmountMinorUnits = 1_000 }, CancellationToken.None);

        topUp.Result.Should().BeOfType<ConflictObjectResult>("a voided key is a 409, never a 400");
        Unwrap(topUp).Body.Code.Should().Be(FiatConsumerErrors.RequestVoidedCode);
        harness.Stripe.SessionsCreated.Should().Be(0);
    }

    [Fact]
    public async Task The_void_and_same_key_409_bodies_serialize_to_the_wire_shapes_clients_build_on()
    {
        var harness = new FiatCheckoutHarness();
        var subject = Guid.NewGuid();
        var open = Unwrap(await harness.StartAsync(subject, "open-key")).Body.Result!;
        var paid = Unwrap(await harness.StartAsync(subject, "paid-key")).Body.Result!;
        harness.Stripe.Settle(paid.IntentId, "complete", "paid");
        var settled = Unwrap(await harness.StartAsync(subject, "settled-key")).Body.Result!;
        harness.Stripe.Settle(settled.IntentId, "complete", "paid");
        await harness.TransitionAsync(settled.IntentId, HostedSessionStates.Completed);

        var tombstone = WireJson(await harness.VoidAsync(subject, "never-sent"));
        var voided = WireJson(await harness.VoidAsync(subject, "open-key"));
        var revealed = WireJson(await harness.VoidAsync(subject, "paid-key"));
        var refused = WireJson(await harness.StartAsync(subject, "never-sent"));
        var finished = WireJson(await harness.StartAsync(subject, "settled-key"));

        tombstone.Should().Be(
            """{"isError":false,"message":"Nothing can be paid under this request key.","result":{"outcome":"voided","intentId":null},"detail":null}""");
        voided.Should().Be(
            $$"""{"isError":false,"message":"Nothing can be paid under this request key.","result":{"outcome":"voided","intentId":"{{open.IntentId}}"},"detail":null}""");
        refused.Should().Be(
            """{"isError":true,"message":"This payment request was cancelled and nothing can be paid under it. Start a new payment.","result":null,"code":"REQUEST_VOIDED","detail":null}""");

        using var document = JsonDocument.Parse(revealed);
        var result = document.RootElement.GetProperty("result");
        result.EnumerateObject().Select(property => property.Name).Should().Equal("outcome", "intentId", "intent");
        result.GetProperty("outcome").GetString().Should().Be("revealed");
        result.GetProperty("intentId").GetGuid().Should().Be(paid.IntentId);
        var intent = result.GetProperty("intent");
        intent.EnumerateObject().Select(property => property.Name).Should().Equal(
            "intentId", "admissionKey", "status", "settlementState", "paymentUrl", "resumed", "amountPaidMinorUnits", "currency", "expiresAt");
        intent.GetProperty("status").GetString().Should().Be(FiatIntentStatuses.Processing);
        intent.GetProperty("settlementState").GetString().Should().Be(FiatSettlementStates.PaymentReceived);

        // INTENT_FINISHED: code, status and message as before; `result` is additive and tells paid from failed.
        using var finishedDocument = JsonDocument.Parse(finished);
        var envelope = finishedDocument.RootElement;
        envelope.EnumerateObject().Select(property => property.Name).Should().Equal("isError", "message", "result", "code", "detail");
        envelope.GetProperty("isError").GetBoolean().Should().BeTrue();
        envelope.GetProperty("code").GetString().Should().Be("INTENT_FINISHED");
        envelope.GetProperty("message").GetString().Should().Be("This payment is already finished.");
        var projection = envelope.GetProperty("result");
        projection.EnumerateObject().Select(property => property.Name).Should().Equal(
            "intentId", "admissionKey", "status", "settlementState", "paymentUrl", "resumed", "amountPaidMinorUnits", "currency", "expiresAt");
        projection.GetProperty("intentId").GetGuid().Should().Be(settled.IntentId);
        projection.GetProperty("status").GetString().Should().Be(FiatIntentStatuses.Complete);
        projection.GetProperty("settlementState").GetString().Should().Be(FiatSettlementStates.PaymentReceived);
        projection.GetProperty("paymentUrl").ValueKind.Should().Be(JsonValueKind.Null);
        projection.GetProperty("amountPaidMinorUnits").GetInt64().Should().Be(1_000);
    }

    /// <summary>MVC's own default serializer settings plus the enum converter Program.cs adds: what goes on the wire.</summary>
    private static readonly JsonSerializerOptions Wire = new(new JsonOptions().JsonSerializerOptions)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static string WireJson<T>(ActionResult<AZOAResult<T>> result) =>
        JsonSerializer.Serialize(result.Result.Should().BeAssignableTo<ObjectResult>().Subject.Value, Wire);

    private static (int Http, AZOAResult<T> Body) Unwrap<T>(ActionResult<AZOAResult<T>> result)
        => FiatCheckoutHarness.Unwrap(result);
}
