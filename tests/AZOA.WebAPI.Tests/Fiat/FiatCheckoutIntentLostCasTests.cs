// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// <see cref="FiatCheckoutIntentEventHandler"/> over the in-memory store when two deliveries race one
/// intent's state CAS (Services/Fiat/AGENTS.md §intent-lost-cas). Only Stripe's read is faked.
/// </summary>
public sealed class FiatCheckoutIntentLostCasTests
{
    private const string Completed = "checkout.session.completed";
    private const string AsyncSucceeded = "checkout.session.async_payment_succeeded";

    [Fact]
    public async Task A_completed_park_that_beats_async_payment_succeeded_fails_the_success_so_its_redelivery_completes_the_intent()
    {
        var (inner, store, intentId) = Seed();
        var parking = Handler(store, StripeReads("complete", "unpaid"));   // `completed` read Stripe before the debit cleared
        var succeeding = Handler(store, StripeReads("complete", "paid"));
        FiatEventOutcome? parked = null;
        store.BeforeTransition = async () => parked = await parking.ApplyAsync(Context(intentId, Completed));

        var first = await succeeding.ApplyAsync(Context(intentId, AsyncSucceeded));

        parked.Should().Be(FiatEventOutcome.AcceptedPending, "the park won the version race");
        first.Should().Be(FiatEventOutcome.Failed, "the success lost its CAS to a different target, so Stripe must redeliver it");
        inner.Peek(intentId)!.State.Should().Be(HostedSessionStates.AwaitingOutcome);

        var redelivered = await succeeding.ApplyAsync(Context(intentId, AsyncSucceeded));

        redelivered.Should().Be(FiatEventOutcome.Applied);
        inner.Peek(intentId)!.State.Should().Be(HostedSessionStates.Completed, "the intent must not stay processing forever");
    }

    [Fact]
    public async Task A_park_that_loses_to_the_same_park_is_still_accepted_pending()
    {
        var (inner, store, intentId) = Seed();
        var webhook = Handler(store, StripeReads("complete", "unpaid"));
        var sweep = Handler(store, StripeReads("complete", "unpaid"));
        store.BeforeTransition = async () => await sweep.ApplyAsync(Context(intentId, Completed));

        var outcome = await webhook.ApplyAsync(Context(intentId, Completed));

        outcome.Should().Be(FiatEventOutcome.AcceptedPending, "the row already reached the park's target; a 500 would only churn Stripe");
        inner.Peek(intentId)!.State.Should().Be(HostedSessionStates.AwaitingOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_payment_event_reaching_a_voided_intent_is_logged_at_Critical(bool voidWinsTheRace)
    {
        var (inner, store, intentId) = Seed();
        var logger = new RecordingLogger();
        var handler = Handler(store, StripeReads("complete", "paid"), logger);
        Func<Task> voidTheKey = async () => (await inner.TryTransitionAsync(intentId, inner.Peek(intentId)!.StateVersion,
            HostedSessionStates.Abandoned, FiatCheckoutVoid.VoidedOutcome, FiatCheckoutVoid.VoidedAfterAdmission, null)).Result.Should().NotBeNull();
        if (voidWinsTheRace)
            store.BeforeTransition = voidTheKey;
        else
            await voidTheKey();

        var outcome = await handler.ApplyAsync(Context(intentId, Completed));

        outcome.Should().Be(FiatEventOutcome.Applied, "the row is terminal; nothing is left to move");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Critical)
            .Which.Message.Should().Contain(intentId.ToString("N")).And.Contain("voided");
    }

    private static (InMemoryHostedSessionStore Inner, InterleavingHostedSessionStore Store, Guid IntentId) Seed()
    {
        var inner = new InMemoryHostedSessionStore();
        var row = inner.SeedAwaitingSubject(HostedSessionFlowKinds.FiatCheckout, FiatRailIds.StripeProviderKey, "cs_1", Guid.NewGuid());
        return (inner, new InterleavingHostedSessionStore(inner), Guid.ParseExact(row.Id, "N"));
    }

    private static IStripeGateway StripeReads(string status, string paymentStatus)
    {
        var gateway = new Mock<IStripeGateway>();
        gateway.Setup(g => g.GetCheckoutSessionAsync(It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<StripeCheckoutSession>.Success(
                new StripeCheckoutSession { SessionId = "cs_1", Status = status, PaymentStatus = paymentStatus, AmountTotalMinorUnits = 1_000 }));
        return gateway.Object;
    }

    private static FiatCheckoutIntentEventHandler Handler(
        IHostedSessionStore store, IStripeGateway gateway, ILogger<FiatCheckoutIntentEventHandler>? logger = null)
    {
        var options = new FiatOptions { Enabled = true, Environment = RailEnvironmentNames.Test, ApiBaseUrl = "https://api.example" };
        options.Test.IsEnabled = true;
        options.Test.SecretKey = "sk_test_x";
        return new FiatCheckoutIntentEventHandler(store, gateway, options,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FiatCheckoutIntentEventHandler>.Instance);
    }

    private static FiatEventContext Context(Guid intentId, string eventType) => new(
        new FiatEvent { Id = "row-" + eventType, ProviderEventId = "evt-" + eventType, EventType = eventType },
        eventType,
        CheckoutSessionId: "cs_1",
        PaymentIntentId: "pi_1",
        ChargeId: null,
        IntentId: intentId,
        SubjectUserId: null,
        RailEnvironment.Test);

    private sealed class RecordingLogger : ILogger<FiatCheckoutIntentEventHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
