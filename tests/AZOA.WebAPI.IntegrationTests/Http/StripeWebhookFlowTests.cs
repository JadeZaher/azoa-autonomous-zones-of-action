using System.Net;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// Stripe webhook deliveries through the real <c>StripeWebhookController</c>, signed exactly as
/// Stripe signs them. See <c>Http/AGENTS.md</c> §flows.
/// </summary>
public sealed class StripeWebhookFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    [Fact]
    public async Task A_payment_counts_only_once_Stripe_itself_reports_it_paid()
    {
        var (intentId, payer, session) = await OpenFundingIntentAsync();
        using var payerClient = Factory.CreateAuthenticatedClientForUser(payer);

        // An asynchronous method: Stripe completes the session but the money has not cleared. The
        // signed body even claims "paid"; the node must re-read Stripe's own session and ignore it.
        session.Status = "complete";
        var completed = StripeVendor.CheckoutEvent(NewEventId(), "checkout.session.completed", session, claimedPaymentStatus: "paid");
        using var completedDelivery = await PostStripeWebhookAsync(completed);
        ShouldKeepTheDelivery(completedDelivery);
        var waiting = await PayerViewAsync(payerClient, payer, intentId);
        waiting.Status.Should().Be(FiatIntentStatuses.Processing);
        waiting.SettlementState.Should().Be(FiatSettlementStates.NotStarted);
        (await IntentAsync(intentId)).AuthoritativeOutcome.Should().NotBe("PAID");

        // The payment clears at Stripe, which then sends the asynchronous success event.
        Flow.Stripe.CompletePayment(session);
        using var clearedDelivery = await PostStripeWebhookAsync(
            StripeVendor.CheckoutEvent(NewEventId(), "checkout.session.async_payment_succeeded", session));
        ShouldKeepTheDelivery(clearedDelivery);

        var paid = await PayerViewAsync(payerClient, payer, intentId);
        paid.Status.Should().Be(FiatIntentStatuses.Complete);
        paid.AmountPaidMinorUnits.Should().Be(2_500);
        // This host registers no price schedule, so the charge is received but cannot be credited.
        paid.SettlementState.Should().Be(FiatSettlementStates.PaymentReceived);
        (await IntentAsync(intentId)).AuthoritativeOutcome.Should().Be("PAID");
    }

    [Fact]
    public async Task A_signed_delivery_moves_the_intent_once_and_redeliveries_are_answered_from_the_inbox()
    {
        var (intentId, _, session) = await OpenFundingIntentAsync();
        Flow.Stripe.Expire(session);
        var eventId = NewEventId();
        var payload = StripeVendor.CheckoutEvent(eventId, "checkout.session.expired", session);

        using var first = await PostStripeWebhookAsync(payload);
        await ShouldReturnAsync(first, HttpStatusCode.OK);
        var applied = (await InboxRowsAsync(eventId)).Should().ContainSingle().Which;
        applied.Status.Should().Be("Applied");
        var intent = await IntentAsync(intentId);
        intent.State.Should().Be(HostedSessionStates.Abandoned);
        intent.AuthoritativeOutcome.Should().Be("EXPIRED");

        // Stripe redelivers the same event, re-signed with a fresh timestamp.
        using var redelivery = await PostStripeWebhookAsync(payload, Flow.Stripe.SignatureHeader(payload, DateTimeOffset.UtcNow.AddSeconds(5)));
        await ShouldReturnAsync(redelivery, HttpStatusCode.OK);
        var afterRedelivery = (await InboxRowsAsync(eventId)).Should().ContainSingle().Which;
        afterRedelivery.AttemptCount.Should().Be(applied.AttemptCount, "a settled event is not dispatched again");
        afterRedelivery.AppliedAt.Should().Be(applied.AppliedAt);
        (await IntentAsync(intentId)).StateVersion.Should().Be(intent.StateVersion);
    }

    public static TheoryData<string> RefusedDeliveries => new() { "wrong-secret", "stale-signature", "livemode-mismatch" };

    [Theory]
    [MemberData(nameof(RefusedDeliveries))]
    public async Task A_delivery_the_node_cannot_authenticate_or_must_not_apply_is_refused_without_an_inbox_row(string variant)
    {
        var (intentId, _, session) = await OpenFundingIntentAsync();
        Flow.Stripe.Expire(session);
        var eventId = NewEventId();
        var payload = StripeVendor.CheckoutEvent(eventId, "checkout.session.expired", session, livemode: variant == "livemode-mismatch");
        var signature = variant switch
        {
            "wrong-secret" => Flow.Stripe.SignatureHeader(payload, secret: "not-the-endpoint-secret-" + Guid.NewGuid().ToString("N")),
            "stale-signature" => Flow.Stripe.SignatureHeader(payload, DateTimeOffset.UtcNow.AddMinutes(-10)),
            _ => Flow.Stripe.SignatureHeader(payload),
        };

        using var response = await PostStripeWebhookAsync(payload, signature);

        // 4xx tells Stripe to stop: none of these can succeed on a retry.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await InboxRowsAsync(eventId)).Should().BeEmpty();
        (await IntentAsync(intentId)).State.Should().Be(HostedSessionStates.AwaitingSubject);
    }

    private static string NewEventId() => "evt_flow_" + Guid.NewGuid().ToString("N");

    /// <summary>A 4xx would make Stripe drop the event for good; owned (2xx) or retry (5xx) both keep it.</summary>
    private static void ShouldKeepTheDelivery(HttpResponseMessage response)
        => ((int)response.StatusCode).Should().NotBeInRange(400, 499, "a payment delivery answered 4xx is never retried by Stripe");

    private async Task<FiatCheckoutIntent> PayerViewAsync(HttpClient payerClient, Guid payer, Guid intentId)
    {
        using var response = await payerClient.GetAsync($"api/fiat/checkout/{payer}/{intentId}");
        return await ReadOkAsync<FiatCheckoutIntent>(response);
    }

    /// <summary>
    /// A funding intent as <c>FiatRailProvider.BeginCheckoutAsync</c> leaves it after Stripe created
    /// the hosted page, with the same session held by the fake Stripe. Arranged directly because
    /// opening one over HTTP first needs a KYC-approved payer; the on-ramp journey covers that path.
    /// </summary>
    private async Task<(Guid IntentId, Guid Payer, StripeCheckoutSessionState Session)> OpenFundingIntentAsync()
    {
        var intentId = Guid.NewGuid();
        var payer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var session = new StripeCheckoutSessionState
        {
            Id = "cs_flow_" + Guid.NewGuid().ToString("N"),
            ClientReferenceId = intentId.ToString("N"),
            AmountTotal = 2_500,
            Currency = "usd",
            ExpiresAt = now.AddMinutes(30).ToUnixTimeSeconds(),
            Metadata = new Dictionary<string, string>
            {
                ["azoa_intent_id"] = intentId.ToString("N"),
                ["azoa_user_id"] = payer.ToString("N"),
            },
        };
        Flow.Stripe.Seed(session);
        await WriteAsync(SurrealWriter.Create(new HostedSession
        {
            Id = intentId.ToString("N"),
            FlowKind = HostedSessionFlowKinds.FiatCheckout,
            ProviderKey = FiatRailIds.StripeProviderKey,
            AdmissionKey = "flow-test:" + intentId.ToString("N"),
            SubjectUserId = SurrealLink.ToLink("user", payer.ToString("N")),
            ProviderSessionId = session.Id,
            RedirectUrl = $"https://checkout.stripe.vendor.test/c/pay/{session.Id}",
            State = HostedSessionStates.AwaitingSubject,
            StateVersion = 1,
            CreationClaimedAt = now,
            CreationStartedAt = now,
            CreationCompletedAt = now,
            CreatedDate = now,
            ModifiedDate = now,
            ExpiresAt = now.AddMinutes(30),
        }));
        return (intentId, payer, session);
    }

    private Task<IReadOnlyList<FiatEvent>> InboxRowsAsync(string providerEventId)
        => RowsAsync(SurrealQuery<FiatEvent>.From().Where(row => row.ProviderEventId == providerEventId));

    private async Task<HostedSession> IntentAsync(Guid intentId)
        => (await RowsAsync(SurrealQuery<HostedSession>.Key(intentId.ToString("N")))).Single();
}
