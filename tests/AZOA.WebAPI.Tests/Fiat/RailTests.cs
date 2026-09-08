// SPDX-License-Identifier: UNLICENSED
// W4.1 — the Fiat.USD rail: the invariants ported from the retired sidecar (pricing, auth,
// webhook mapping, idempotency, errors) and the boot guards that keep a node from advertising
// a money path it cannot complete.

using System.Net;
using System.Reflection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public class RailTests
{
    private const string WebhookSecret = "whsec_test_only_not_a_real_secret";
    private const string ReturnOrigin = "https://app.example";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    // ── The signature is the webhook's ONLY authority ─────────────────────────

    [Fact]
    public void Verify_accepts_a_body_signed_with_the_configured_secret()
    {
        const string body = "{\"id\":\"evt_1\",\"type\":\"checkout.session.completed\"}";
        var timestamp = Now.ToUnixTimeSeconds();
        var header = $"t={timestamp},v1={StripeWebhookSignature.ComputeSignature(body, timestamp, WebhookSecret)}";

        StripeWebhookSignature
            .Verify(body, header, WebhookSecret, Now, TimeSpan.FromMinutes(5))
            .Should().Be(StripeSignatureVerdict.Valid);
    }

    [Fact]
    public void Verify_rejects_a_body_that_changed_after_it_was_signed()
    {
        const string signed = "{\"id\":\"evt_1\",\"amount_total\":100}";
        const string tampered = "{\"id\":\"evt_1\",\"amount_total\":1000000}";
        var timestamp = Now.ToUnixTimeSeconds();
        var header = $"t={timestamp},v1={StripeWebhookSignature.ComputeSignature(signed, timestamp, WebhookSecret)}";

        StripeWebhookSignature
            .Verify(tampered, header, WebhookSecret, Now, TimeSpan.FromMinutes(5))
            .Should().Be(StripeSignatureVerdict.Mismatch);
    }

    [Fact]
    public void Verify_rejects_a_captured_delivery_replayed_outside_the_tolerance()
    {
        const string body = "{\"id\":\"evt_1\"}";
        var timestamp = Now.AddMinutes(-30).ToUnixTimeSeconds();
        var header = $"t={timestamp},v1={StripeWebhookSignature.ComputeSignature(body, timestamp, WebhookSecret)}";

        StripeWebhookSignature
            .Verify(body, header, WebhookSecret, Now, TimeSpan.FromMinutes(5))
            .Should().Be(StripeSignatureVerdict.Stale);
    }

    [Fact]
    public void Verify_fails_closed_when_no_webhook_secret_is_configured()
    {
        const string body = "{\"id\":\"evt_1\"}";
        var timestamp = Now.ToUnixTimeSeconds();
        var header = $"t={timestamp},v1={StripeWebhookSignature.ComputeSignature(body, timestamp, WebhookSecret)}";

        // A node with no secret verifies nothing, so it must accept nothing — never "no secret
        // configured, therefore no check to fail".
        StripeWebhookSignature
            .Verify(body, header, webhookSecret: "", Now, TimeSpan.FromMinutes(5))
            .Should().Be(StripeSignatureVerdict.NotConfigured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1=deadbeef")]
    [InlineData("t=notanumber,v1=deadbeef")]
    [InlineData("t=1757332800")]
    public void Verify_rejects_a_header_it_cannot_parse(string header)
    {
        StripeWebhookSignature
            .Verify("{}", header, WebhookSecret, Now, TimeSpan.FromMinutes(5))
            .Should().BeOneOf(StripeSignatureVerdict.Malformed, StripeSignatureVerdict.Mismatch);
    }

    [Fact]
    public void Verify_accepts_a_header_carrying_several_signatures()
    {
        // Stripe sends one v1 per active endpoint secret during a rotation. Matching ANY is
        // correct; matching only the first would break every rotation.
        const string body = "{\"id\":\"evt_1\"}";
        var timestamp = Now.ToUnixTimeSeconds();
        var header = $"t={timestamp},v1=00000000,v1={StripeWebhookSignature.ComputeSignature(body, timestamp, WebhookSecret)}";

        StripeWebhookSignature
            .Verify(body, header, WebhookSecret, Now, TimeSpan.FromMinutes(5))
            .Should().Be(StripeSignatureVerdict.Valid);
    }

    // ── Webhook mapping: retryable vs terminal (ported) ───────────────────────

    [Theory]
    [InlineData(FiatEventOutcome.Applied, 200)]
    [InlineData(FiatEventOutcome.Ignored, 202)]
    [InlineData(FiatEventOutcome.AcceptedPending, 202)]
    [InlineData(FiatEventOutcome.Failed, 500)]
    public void Outcomes_map_to_the_ported_status_codes(FiatEventOutcome outcome, int expected)
    {
        // AcceptedPending MUST be a 2xx. Returning an error for work that is merely still in
        // flight makes the provider retry until it gives up, leaving a person charged with
        // nothing credited — the exact failure the sidecar's mapping table existed to prevent.
        FiatEventDispositions.ToStatusCode(outcome).Should().Be(expected);
    }

    [Fact]
    public void The_worst_handler_outcome_decides_the_delivery()
    {
        FiatEventDispositions.Aggregate(new[] { FiatEventOutcome.Applied, FiatEventOutcome.Failed })
            .Should().Be(FiatEventOutcome.Failed);

        FiatEventDispositions.Aggregate(new[] { FiatEventOutcome.Applied, FiatEventOutcome.AcceptedPending })
            .Should().Be(FiatEventOutcome.AcceptedPending);

        FiatEventDispositions.Aggregate(new[] { FiatEventOutcome.Ignored, FiatEventOutcome.Applied })
            .Should().Be(FiatEventOutcome.Applied);

        FiatEventDispositions.Aggregate(Array.Empty<FiatEventOutcome>())
            .Should().Be(FiatEventOutcome.Ignored);
    }

    [Fact]
    public void Work_still_in_flight_leaves_the_inbox_row_unsettled()
    {
        // Marking an unconfirmed issuance "Applied" is what makes a stuck credit invisible.
        FiatEventDispositions.ToRowStatus(FiatEventOutcome.AcceptedPending)
            .Should().Be(FiatEventStatuses.Received);
        FiatEventDispositions.ToRowStatus(FiatEventOutcome.Applied)
            .Should().Be(FiatEventStatuses.Applied);
        FiatEventDispositions.ToRowStatus(FiatEventOutcome.Failed)
            .Should().Be(FiatEventStatuses.Failed);
    }

    // ── Pricing: a client can never name a quantity ───────────────────────────

    [Fact]
    public async Task The_checkout_request_carries_no_quantity_only_the_money_amount()
    {
        var capture = new CapturingHandler("{\"id\":\"cs_test_1\",\"url\":\"https://pay.example/c\",\"status\":\"open\",\"payment_status\":\"unpaid\",\"livemode\":false}");
        var gateway = new StripeGateway(new SingleClientFactory(capture), NullLogger<StripeGateway>.Instance);

        await gateway.CreateCheckoutSessionAsync(
            new StripeCredentials("https://api.example", "sk_test_x", TimeSpan.FromSeconds(5)),
            new StripeCheckoutSessionRequest
            {
                IntentId = "11111111111111111111111111111111",
                AdmissionKey = "user:key",
                SubjectUserId = "22222222222222222222222222222222",
                Purpose = FiatCheckoutPurposes.FundsTopUp,
                Currency = "usd",
                AmountMinorUnits = 2500,
                ProductName = "Account funds",
                SuccessUrl = ReturnOrigin + "/done?intent=11111111111111111111111111111111",
                CancelUrl = ReturnOrigin + "/cancel?intent=11111111111111111111111111111111",
                IdempotencyKey = "azoa-fiat-checkout-11111111111111111111111111111111",
            });

        // Unescaped so the assertion is about the FIELDS, not about how the encoder spells '['.
        var form = Uri.UnescapeDataString(capture.LastBody!);

        // The money the person pays is the ONLY amount on the wire, and it is the one asked for.
        form.Should().Contain("line_items[0][price_data][unit_amount]=2500");

        // Metadata is identity and routing. No quantity, no rate, no unit — a client that could
        // put one there is the money-printer the sidecar's pricing section closed.
        form.Should().Contain("metadata[azoa_intent_id]=11111111111111111111111111111111");
        foreach (var forbidden in new[] { "quantity]", "amount]", "units]", "rate]", "token]" })
            form.Should().NotContain("metadata[azoa_" + forbidden);
    }

    [Fact]
    public void The_webhook_envelope_exposes_no_amount_at_all()
    {
        // Structural, not stylistic: the only figure that may become funds is the one re-read
        // from the provider. A future property called anything amount-shaped on this type would
        // give a handler a body-supplied number to trust.
        var suspicious = typeof(StripeEventEnvelope)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name =>
                name.Contains("Amount", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Total", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Quantity", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Units", StringComparison.OrdinalIgnoreCase))
            .ToList();

        suspicious.Should().BeEmpty(
            "the webhook body is a trigger, never an amount authority; re-read the session instead");
    }

    [Fact]
    public void The_envelope_recovers_the_node_s_own_intent_from_provider_correlation()
    {
        var envelope = StripeEventEnvelope.TryParse(
            "{\"id\":\"evt_9\",\"type\":\"checkout.session.completed\",\"livemode\":false,\"created\":1757332800,"
            + "\"data\":{\"object\":{\"id\":\"cs_9\",\"object\":\"checkout.session\",\"payment_intent\":\"pi_9\","
            + "\"client_reference_id\":\"33333333333333333333333333333333\","
            + "\"metadata\":{\"azoa_user_id\":\"44444444444444444444444444444444\"}}}}");

        envelope.Should().NotBeNull();
        envelope!.EventId.Should().Be("evt_9");
        envelope.CheckoutSessionId.Should().Be("cs_9");
        envelope.PaymentIntentId.Should().Be("pi_9");
        envelope.IntentId.Should().Be(Guid.ParseExact("33333333333333333333333333333333", "N"));
        envelope.SubjectUserId.Should().Be(Guid.ParseExact("44444444444444444444444444444444", "N"));
    }

    [Fact]
    public void One_provider_event_id_always_produces_one_inbox_row_id()
    {
        // The record id IS the dedup: a redelivery loses the CREATE instead of running twice.
        FiatEventFactory.RecordIdFor("stripe", "evt_1")
            .Should().Be(FiatEventFactory.RecordIdFor("STRIPE", " evt_1 "));
        FiatEventFactory.RecordIdFor("stripe", "evt_1")
            .Should().NotBe(FiatEventFactory.RecordIdFor("stripe", "evt_2"));
    }

    [Fact]
    public void The_inbox_row_stores_a_digest_and_not_the_body()
    {
        const string body = "{\"id\":\"evt_5\",\"type\":\"charge.refunded\"}";
        var envelope = StripeEventEnvelope.TryParse(body)!;

        var row = FiatEventFactory.Create(envelope, body, RailEnvironment.Test, Now);

        row.PayloadDigest.Should().HaveLength(64).And.NotContain("evt_5");
        row.Status.Should().Be(FiatEventStatuses.Received);
        row.Environment.Should().Be("test");
    }

    // ── Return URLs: the allowlist is the open-redirect guard ─────────────────

    [Fact]
    public void A_return_url_on_an_allowlisted_origin_is_accepted_and_carries_the_intent()
    {
        FiatReturnUrlPolicy.TryResolveOrigin(
            ReturnOrigin + "/funds/done", configuredDefault: null, new[] { ReturnOrigin },
            RailEnvironment.Live, out var resolved, out _).Should().BeTrue();

        FiatReturnUrlPolicy.AppendIntent(resolved!, "55555555555555555555555555555555")
            .Should().Contain("intent=55555555555555555555555555555555");
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("https://app.example.evil.example/steal")]
    [InlineData("http://app.example/done")]
    [InlineData("https://app.example:8443/done")]
    [InlineData("https://trusted@evil.example/done")]
    [InlineData("/relative/done")]
    [InlineData("")]
    public void A_return_url_off_the_allowlist_is_refused(string candidate)
    {
        FiatReturnUrlPolicy.TryResolveOrigin(
            candidate, configuredDefault: null, new[] { ReturnOrigin },
            RailEnvironment.Live, out _, out var reason).Should().BeFalse();

        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_empty_allowlist_permits_nothing()
    {
        // An empty list must not read as "unrestricted"; that is how a forgotten config key
        // becomes an open redirect on a payment page.
        FiatReturnUrlPolicy.TryResolveOrigin(
            ReturnOrigin + "/done", configuredDefault: null, Array.Empty<string>(),
            RailEnvironment.Live, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void An_existing_query_string_survives_the_intent_parameter()
    {
        FiatReturnUrlPolicy.TryResolveOrigin(
            ReturnOrigin + "/done?ref=abc", configuredDefault: null, new[] { ReturnOrigin },
            RailEnvironment.Live, out var resolved, out _).Should().BeTrue();

        var url = FiatReturnUrlPolicy.AppendIntent(resolved!, "66666666666666666666666666666666");
        url.Should().Contain("ref=abc").And.Contain("intent=66666666666666666666666666666666");
    }

    // ── Boot guards: capability honesty on the money path ─────────────────────

    [Fact]
    public void A_disabled_rail_boots_without_any_secret()
    {
        var act = () => FiatRailStartup.Validate(
            new FiatOptions { Enabled = false }, simulatedBlockchainMode: false, settlementHandlerRegistered: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void An_unrecognised_environment_refuses_to_boot_even_when_the_rail_is_off()
    {
        var act = () => FiatRailStartup.Validate(
            new FiatOptions { Enabled = false, Environment = "livee" },
            simulatedBlockchainMode: false, settlementHandlerRegistered: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Fiat:Environment*");
    }

    [Fact]
    public void An_enabled_rail_without_a_webhook_secret_refuses_to_boot()
    {
        var options = EnabledOptions(RailEnvironmentNames.Test);
        options.Test.WebhookSecret = string.Empty;

        var act = () => FiatRailStartup.Validate(
            options, simulatedBlockchainMode: false, settlementHandlerRegistered: true);

        // Without it every notification fails verification: charged, never credited.
        act.Should().Throw<InvalidOperationException>().WithMessage("*WebhookSecret*");
    }

    [Fact]
    public void An_enabled_rail_with_no_return_origins_refuses_to_boot()
    {
        var options = EnabledOptions(RailEnvironmentNames.Test);
        options.Checkout.AllowedReturnOrigins.Clear();

        var act = () => FiatRailStartup.Validate(
            options, simulatedBlockchainMode: false, settlementHandlerRegistered: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedReturnOrigins*");
    }

    [Fact]
    public void The_live_environment_refuses_to_boot_with_nothing_able_to_credit_a_payment()
    {
        var act = () => FiatRailStartup.Validate(
            EnabledOptions(RailEnvironmentNames.Live),
            simulatedBlockchainMode: false,
            settlementHandlerRegistered: false);

        // This is the guard that stops the whole "the settlement handler ships next" failure
        // mode: a real card cannot be charged while no IFiatSettlementHandler exists.
        act.Should().Throw<InvalidOperationException>().WithMessage("*IFiatSettlementHandler*");
    }

    [Fact]
    public void The_live_environment_refuses_to_boot_against_simulated_settlement()
    {
        var act = () => FiatRailStartup.Validate(
            EnabledOptions(RailEnvironmentNames.Live),
            simulatedBlockchainMode: true,
            settlementHandlerRegistered: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Simulated*");
    }

    [Fact]
    public void A_fully_configured_live_rail_boots()
    {
        var act = () => FiatRailStartup.Validate(
            EnabledOptions(RailEnvironmentNames.Live),
            simulatedBlockchainMode: false,
            settlementHandlerRegistered: true);

        act.Should().NotThrow();
    }

    // ── Environment binding: one at a time, fail-closed ───────────────────────

    [Fact]
    public void Only_the_selected_environment_resolves_an_endpoint()
    {
        var options = EnabledOptions(RailEnvironmentNames.Test);
        options.Live.IsEnabled = true;
        options.Live.SecretKey = "sk_live_x";

        options.ResolveEndpoint(RailEnvironment.Test).Should().NotBeNull();

        // Binding both would let a test-mode checkout be settled by a live webhook in one process.
        options.ResolveEndpoint(RailEnvironment.Live).Should().BeNull();
    }

    [Fact]
    public void A_disabled_rail_or_a_missing_secret_resolves_no_endpoint_at_all()
    {
        var disabled = EnabledOptions(RailEnvironmentNames.Test);
        disabled.Enabled = false;
        disabled.ResolveEndpoint(RailEnvironment.Test).Should().BeNull();

        var secretless = EnabledOptions(RailEnvironmentNames.Test);
        secretless.Test.SecretKey = string.Empty;
        secretless.ResolveEndpoint(RailEnvironment.Test).Should().BeNull();
    }

    [Fact]
    public void The_capability_report_names_missing_keys_and_never_values()
    {
        var options = EnabledOptions(RailEnvironmentNames.Test);
        options.Test.WebhookSecret = string.Empty;

        var missing = options.MissingKeysFor(RailEnvironment.Test);

        missing.Should().Contain("Fiat:Test:WebhookSecret");
        missing.Should().NotContain(key => key.Contains("sk_test", StringComparison.Ordinal));
    }

    // ── The rail refuses what it cannot honestly do ───────────────────────────

    [Fact]
    public async Task The_rail_refuses_a_generic_transfer_rather_than_faking_a_payment()
    {
        var rail = BuildRail(EnabledOptions(RailEnvironmentNames.Test), Mock.Of<IStripeGateway>());

        var result = await rail.TransferAsync(
            tokenId: "usd",
            fromAddress: "a",
            toAddress: "b",
            amount: RailAmount.OfUsdMinorUnits(100));

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
    }

    [Fact]
    public async Task The_rail_refuses_an_amount_that_is_not_money()
    {
        var rail = BuildRail(EnabledOptions(RailEnvironmentNames.Test), Mock.Of<IStripeGateway>());

        // Chain base units reinterpreted as cents would move a completely different quantity.
        var result = await rail.TransferAsync(
            tokenId: "1001", fromAddress: "a", toAddress: "b", amount: RailAmount.OfChainAsset(100, "1001"));

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task An_unreadable_balance_is_an_error_and_never_a_zero()
    {
        var rail = BuildRail(EnabledOptions(RailEnvironmentNames.Test), Mock.Of<IStripeGateway>());

        var result = await rail.GetBalanceAsync("anything");

        result.IsError.Should().BeTrue();
        result.Result.Should().BeNull();
    }

    [Fact]
    public void The_rail_declares_a_chargeback_window_rather_than_finality()
    {
        var options = EnabledOptions(RailEnvironmentNames.Test);
        options.ChargebackWindowDays = 90;

        var profile = BuildRail(options, Mock.Of<IStripeGateway>()).SettlementProfile;

        profile.Reversibility.Should().Be(SettlementReversibility.ReversibleWithinWindow);
        profile.TryGetReleaseTimeUtc(Now, out var releaseAt).Should().BeTrue();
        releaseAt.Should().Be(Now.AddDays(90));
    }

    [Fact]
    public void A_bound_rail_cannot_be_retargeted_at_another_environment()
    {
        var rail = BuildRail(EnabledOptions(RailEnvironmentNames.Test), Mock.Of<IStripeGateway>());
        rail.InitializeRail(new ValueRailBinding(RailEnvironment.Test, new BlockchainNetworkConfig()));

        var act = () => rail.InitializeRail(
            new ValueRailBinding(RailEnvironment.Live, new BlockchainNetworkConfig()));

        // A cached instance that could be rebound would let one resolve move another caller's
        // in-flight payment from test to live.
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("complete", "paid", ChainConfirmation.Confirmed)]
    [InlineData("complete", "unpaid", ChainConfirmation.Pending)]
    [InlineData("open", "unpaid", ChainConfirmation.Pending)]
    [InlineData("expired", "unpaid", ChainConfirmation.FailedOnChain)]
    [InlineData("something_new", "unpaid", ChainConfirmation.Unknown)]
    public void A_session_is_classified_by_what_actually_cleared(
        string status, string paymentStatus, ChainConfirmation expected)
    {
        // "complete" alone is not payment: an asynchronous method can leave a completed session
        // unpaid, and treating that as confirmed would credit funds nobody sent.
        FiatRailProvider
            .ClassifySession(new StripeCheckoutSession { Status = status, PaymentStatus = paymentStatus })
            .Should().Be(expected);
    }

    // ── Intent bookkeeping credits nothing ───────────────────────────────────

    [Fact]
    public void The_intent_handler_is_not_a_settlement_handler()
    {
        // The live-environment boot guard counts settlement handlers. If bookkeeping counted, the
        // guard would pass on a node where nothing can credit a payment.
        typeof(IFiatSettlementHandler)
            .IsAssignableFrom(typeof(FiatCheckoutIntentEventHandler))
            .Should().BeFalse();
    }

    [Fact]
    public async Task An_expired_payment_page_abandons_its_intent()
    {
        var session = FiatSession();
        var store = new Mock<IHostedSessionStore>();
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<HostedSession>.Success(session));
        store.Setup(s => s.TryTransitionAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<HostedSession>.Success(session));

        var handler = new FiatCheckoutIntentEventHandler(
            store.Object, Mock.Of<IStripeGateway>(), EnabledOptions(RailEnvironmentNames.Test),
            NullLogger<FiatCheckoutIntentEventHandler>.Instance);

        var outcome = await handler.ApplyAsync(Context(session, "checkout.session.expired"));

        outcome.Should().Be(FiatEventOutcome.Applied);
        store.Verify(s => s.TryTransitionAsync(
            It.IsAny<Guid>(), It.IsAny<long>(), HostedSessionStates.Abandoned,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_redelivery_after_the_terminal_write_moves_nothing()
    {
        var session = FiatSession();
        session.State = HostedSessionStates.Completed;

        var store = new Mock<IHostedSessionStore>();
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<HostedSession>.Success(session));

        var handler = new FiatCheckoutIntentEventHandler(
            store.Object, Mock.Of<IStripeGateway>(), EnabledOptions(RailEnvironmentNames.Test),
            NullLogger<FiatCheckoutIntentEventHandler>.Instance);

        var outcome = await handler.ApplyAsync(Context(session, "checkout.session.completed"));

        outcome.Should().Be(FiatEventOutcome.Applied);
        store.Verify(s => s.TryTransitionAsync(
            It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_session_that_has_not_cleared_parks_instead_of_completing()
    {
        var session = FiatSession();
        var store = new Mock<IHostedSessionStore>();
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<HostedSession>.Success(session));
        store.Setup(s => s.TryTransitionAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<HostedSession>.Success(session));

        var gateway = new Mock<IStripeGateway>();
        gateway.Setup(g => g.GetCheckoutSessionAsync(
                It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<StripeCheckoutSession>.Success(
                new StripeCheckoutSession { SessionId = "cs_1", Status = "complete", PaymentStatus = "unpaid" }));

        var handler = new FiatCheckoutIntentEventHandler(
            store.Object, gateway.Object, EnabledOptions(RailEnvironmentNames.Test),
            NullLogger<FiatCheckoutIntentEventHandler>.Instance);

        var outcome = await handler.ApplyAsync(Context(session, "checkout.session.completed"));

        // The provider must stop retrying, and the intent must not terminalise on a payment that
        // has not cleared.
        outcome.Should().Be(FiatEventOutcome.AcceptedPending);
        store.Verify(s => s.TryTransitionAsync(
            It.IsAny<Guid>(), It.IsAny<long>(), HostedSessionStates.AwaitingOutcome,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static FiatOptions EnabledOptions(string environment)
    {
        var options = new FiatOptions
        {
            Enabled = true,
            Environment = environment,
            ApiBaseUrl = "https://api.example",
            ChargebackWindowDays = 120,
        };

        options.Test.IsEnabled = true;
        options.Test.SecretKey = "sk_test_x";
        options.Test.WebhookSecret = WebhookSecret;
        options.Live.IsEnabled = true;
        options.Live.SecretKey = "sk_live_x";
        options.Live.WebhookSecret = WebhookSecret;
        options.Checkout.AllowedReturnOrigins.Add(ReturnOrigin);
        options.Checkout.AllowedPurposes.Add(FiatCheckoutPurposes.FundsTopUp);
        return options;
    }

    private static FiatRailProvider BuildRail(FiatOptions options, IStripeGateway gateway)
        => new(options, gateway, new NoScopeFactory(), NullLogger<FiatRailProvider>.Instance);

    private static HostedSession FiatSession() => new()
    {
        Id = "77777777777777777777777777777777",
        FlowKind = HostedSessionFlowKinds.FiatCheckout,
        ProviderKey = FiatRailIds.StripeProviderKey,
        AdmissionKey = "user:key",
        SubjectUserId = "88888888888888888888888888888888",
        State = HostedSessionStates.AwaitingSubject,
        StateVersion = 1,
        ProviderSessionId = "cs_1",
    };

    private static FiatEventContext Context(HostedSession session, string eventType) => new(
        new FiatEvent { Id = "row", ProviderEventId = "evt_1", EventType = eventType },
        eventType,
        CheckoutSessionId: "cs_1",
        PaymentIntentId: "pi_1",
        ChargeId: null,
        IntentId: Guid.ParseExact(session.Id, "N"),
        SubjectUserId: null,
        RailEnvironment.Test);

    /// <summary>Captures the exact form body the gateway put on the wire.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _response;

        public CapturingHandler(string response) => _response = response;

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>The rail resolves scoped services per call; these tests never reach that path.</summary>
    private sealed class NoScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("This test must not reach a scoped dependency.");
    }
}
