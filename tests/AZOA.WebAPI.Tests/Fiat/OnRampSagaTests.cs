// SPDX-License-Identifier: UNLICENSED
// W4.2 -- the fiat on-ramp saga: a cleared card payment becomes units, at a price this node
// registered beforehand, with a lot row that records which units that money bought.
//
// Every test here pins one control that a prose claim would otherwise stand in for:
//   * the quantity comes from the provider's own confirmed total x a registered price, and from
//     nothing a client can influence;
//   * the recipient comes from the durable funding intent, not from provider metadata;
//   * one payment credits at most once;
//   * a payment this node will not credit becomes a VISIBLE, refundable lot rather than either an
//     endless provider retry or a silently swallowed charge.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public class OnRampSagaTests
{
    private const string ChainType = "Algorand";
    private const string AssetId = "1001";
    private const string UnitName = "ARDA";
    private const string SessionId = "cs_test_1";
    private const string PaymentIntentId = "pi_test_1";
    private const string ChargeId = "ch_test_1";

    /// <summary>The settled base: 1 whole unit = $0.01 at 2 decimals, so one cent buys 100 base units.</summary>
    private const decimal SettledBaseRate = 100m;

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid IntentId = new("11111111111111111111111111111111");
    private static readonly Guid SubjectUserId = new("22222222222222222222222222222222");
    private static readonly Guid TenantUserId = new("33333333333333333333333333333333");

    // ── The money-printer: a quantity may only come from the provider total x a registered price ──

    [Fact]
    public async Task Pending_issuance_is_not_aged_until_the_same_operation_is_confirmed()
    {
        var harness = new Harness { IssuanceStatus = "Pending" };

        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.AcceptedPending);
        var pending = harness.Lots.Single();
        pending.Status.Should().NotBe(OnRampLotStatuses.Issued);
        pending.OperationId.Should().Be(harness.IssuanceId.ToString("N"));
        pending.IssuedAt.Should().BeNull();
        pending.ReversalWindowEnds.Should().BeNull();
        var allocationKey = harness.CapturedIdempotencyKey;

        harness.IssuanceStatus = OperationStatus.Completed;
        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.Applied);
        harness.Lots.Single().OperationId.Should().Be(pending.OperationId);
        harness.Lots.Single().IssuedAt.Should().Be(Now);
        harness.CapturedIdempotencyKey.Should().Be(allocationKey);
    }

    [Fact]
    public async Task Terminal_operation_without_transaction_evidence_does_not_issue_a_lot()
    {
        var harness = new Harness { IssuanceTransactionId = string.Empty };

        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.AcceptedPending);
        harness.Lots.Single().Status.Should().NotBe(OnRampLotStatuses.Issued);
        harness.Lots.Single().IssuedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_issued_quantity_is_the_provider_total_times_the_registered_price()
    {
        var harness = new Harness();

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Applied);

        // $10.00 = 1000 cents; 1000 x 100 base units per cent = 100_000 base units.
        harness.CapturedRequest.Should().NotBeNull();
        harness.CapturedRequest!.Amount.Should().Be("100000");
        harness.CapturedRequest.Kind.Should().Be(AllocationKind.IssueUnits);
        harness.CapturedRequest.AssetId.Should().Be(AssetId);
        harness.CapturedRequest.ChainType.Should().Be(ChainType);
        harness.CapturedRequest.Partition.Should().Be("on-ramp");
        harness.CapturedRequest.Purpose.Should().Be(OnRampSettlementHandler.IssuancePurpose);
    }

    [Fact]
    public async Task The_issued_quantity_tracks_the_re_read_session_and_not_the_delivery()
    {
        // The delivery is identical in every test: FiatEvent carries no amount and
        // FiatEventContext exposes none, so there is nothing a forged body could say. Only the
        // server-side re-read moves, and the issued figure moves with it.
        var harness = new Harness();
        harness.Session = MakeSession(amountTotalMinorUnits: 250L);

        await harness.ApplyAsync();

        harness.CapturedRequest!.Amount.Should().Be("25000");
        harness.Lots.Single().FiatMinorUnits.Should().Be(250L);
    }

    [Fact]
    public async Task A_price_registered_by_the_tenant_prices_that_tenants_payment()
    {
        var harness = new Harness { IntentTenantId = TenantUserId };
        harness.Schedules[PriceScheduleScope.For(TenantUserId)] = Schedule(scopeKey: TenantUserId.ToString("N"), rate: 250m, version: 7);

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Applied);
        harness.CapturedRequest!.Amount.Should().Be("250000");
        harness.Lots.Single().PriceScheduleVersion.Should().Be(7);
    }

    [Fact]
    public async Task A_tenant_with_no_registered_price_is_not_priced_by_the_operators_default()
    {
        // Substituting one party's price for another's is a silent economic decision. The default
        // refusal is retryable: the tenant registers a price and the provider's own retry credits.
        var harness = new Harness { IntentTenantId = TenantUserId };

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.CapturedRequest.Should().BeNull();
        harness.Lots.Single().Status.Should().Be(OnRampLotStatuses.Pending);
    }

    [Fact]
    public async Task The_operators_default_stands_in_only_when_the_operator_allowed_it()
    {
        var harness = new Harness { IntentTenantId = TenantUserId };
        harness.Options.AllowNodeDefaultScheduleFallback = true;

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Applied);
        harness.CapturedRequest!.Amount.Should().Be("100000");
    }

    [Fact]
    public async Task A_retired_price_is_not_replaced_by_an_older_active_one()
    {
        var harness = new Harness();
        harness.Schedules[PriceScheduleScope.Node] =
            Schedule(rate: SettledBaseRate, version: 4, status: PriceScheduleStatuses.Retired);

        var outcome = await harness.ApplyAsync();

        // "We have stopped selling units" is not "use last week's price".
        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task A_payment_too_small_to_buy_a_whole_unit_is_refused_and_never_issued_as_zero()
    {
        var harness = new Harness();
        harness.Session = MakeSession(amountTotalMinorUnits: 1L);
        harness.Schedules[PriceScheduleScope.Node] = Schedule(rate: 0.5m, version: 1);

        var outcome = await harness.ApplyAsync();

        // The provider stops retrying, and the charge stays visible as money the node owes back.
        outcome.Should().Be(FiatEventOutcome.AcceptedPending);
        harness.CapturedRequest.Should().BeNull();
        harness.Lots.Single().Status.Should().Be(OnRampLotStatuses.Refused);
        harness.Lots.Single().StatusReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── The lot: the only per-issuance provenance a fungible asset has ──

    [Fact]
    public async Task An_issuance_writes_a_lot_carrying_the_money_the_price_and_the_source_charge()
    {
        var harness = new Harness();

        await harness.ApplyAsync();

        var lot = harness.Lots.Single();
        lot.Status.Should().Be(OnRampLotStatuses.Issued);
        lot.SubjectUserId.Should().Be(SubjectUserId.ToString("N"));
        lot.IntentId.Should().Be(IntentId.ToString("N"));
        lot.FiatMinorUnits.Should().Be(1000L);
        lot.CurrencyCode.Should().Be("USD");
        lot.GrossQuantityBaseUnits.Should().Be("100000");
        lot.QuantityBaseUnits.Should().Be("100000");
        lot.UnitsPerMinorUnit.Should().Be("100");
        lot.PriceScheduleVersion.Should().Be(1);
        lot.SourceChargeId.Should().Be(ChargeId);
        lot.SourcePaymentIntentId.Should().Be(PaymentIntentId);
        lot.SourceCheckoutSessionId.Should().Be(SessionId);
        lot.OperationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_lot_ages_against_the_rails_own_chargeback_window_not_a_local_constant()
    {
        var harness = new Harness { ChargebackWindowDays = 90 };

        await harness.ApplyAsync();

        var lot = harness.Lots.Single();
        lot.IssuedAt.Should().Be(Now);
        lot.ReversalWindowEnds.Should().Be(Now.AddDays(90));
    }

    [Fact]
    public async Task Units_are_not_issued_at_all_when_the_rail_cannot_say_how_long_to_age_them()
    {
        var harness = new Harness { RailResolvable = false };

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task The_lot_records_what_the_person_received_after_the_node_fee()
    {
        var harness = new Harness { NetAmount = "99000", NodeFeeAmount = "1000" };

        await harness.ApplyAsync();

        var lot = harness.Lots.Single();
        lot.GrossQuantityBaseUnits.Should().Be("100000");
        lot.QuantityBaseUnits.Should().Be("99000");   // W8.1 may only age and consume this
        lot.NodeFeeBaseUnits.Should().Be("1000");
    }

    // ── One payment credits at most once ──

    [Fact]
    public async Task A_redelivery_of_a_credited_payment_does_not_issue_a_second_time()
    {
        var harness = new Harness();

        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.Applied);
        harness.CapturedRequest = null;

        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.Applied);

        harness.CapturedRequest.Should().BeNull("a redelivery must never raise a second issuance");
        harness.Lots.Should().HaveCount(1);
    }

    [Fact]
    public void One_funding_intent_settles_into_exactly_one_lot()
    {
        OnRampLot.RecordIdFor(IntentId.ToString("N"))
            .Should().Be(OnRampLot.RecordIdFor(IntentId.ToString("N").ToUpperInvariant()));
    }

    [Fact]
    public async Task The_issuance_claims_a_deterministic_idempotency_key_so_a_retry_replays()
    {
        var harness = new Harness();

        await harness.ApplyAsync();

        harness.CapturedIdempotencyKey
            .Should().Be(OnRampSettlementHandler.IssuanceIdempotencyKey(IntentId.ToString("N")));
    }

    [Fact]
    public async Task The_issuance_asks_the_reserve_on_the_operators_authority_and_not_on_a_missing_tenant()
    {
        // OI-21. This rail draws the node's OWN unit with no acting tenant. The reserve no
        // longer reads that absence as the operator, so the rail has to SAY so: without the
        // positive signal the draw is refused, and with it no user-driven run can borrow the
        // same privilege by simply having no tenant either.
        var harness = new Harness();

        await harness.ApplyAsync();

        harness.CapturedDrawAuthority.Should().NotBeNull();
        harness.CapturedDrawAuthority!.IsNodeOperator.Should().BeTrue();
        harness.CapturedDrawAuthority.ActingTenantId.Should().BeNull();
    }

    // ── The recipient is the intent's, never the webhook's ──

    [Fact]
    public async Task Metadata_naming_a_different_account_than_the_intent_credits_nobody()
    {
        var harness = new Harness { MetadataSubjectUserId = TenantUserId };

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.CapturedRequest.Should().BeNull();
        harness.Lots.Should().BeEmpty();
    }

    [Fact]
    public async Task A_session_whose_provider_correlation_names_another_intent_credits_nobody()
    {
        var harness = new Harness();
        harness.Session = MakeSession(clientReferenceId: "99999999999999999999999999999999");

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task A_delivery_with_no_intent_correlation_credits_nobody()
    {
        var harness = new Harness { DeliveryIntentId = null };

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Ignored);
        harness.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task A_session_from_the_other_mode_never_settles_value()
    {
        var harness = new Harness();
        harness.Session = MakeSession(livemode: true);   // the rail is bound to test

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.CapturedRequest.Should().BeNull();
    }

    // ── Payment states ──

    [Fact]
    public async Task An_asynchronous_payment_that_has_not_cleared_issues_nothing_and_is_not_retried()
    {
        var harness = new Harness();
        harness.Session = MakeSession(paymentStatus: "unpaid");

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.AcceptedPending);
        harness.CapturedRequest.Should().BeNull();
        harness.Lots.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_session_credits_nothing()
    {
        var harness = new Harness();
        harness.Session = MakeSession(status: "expired", paymentStatus: "unpaid");

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Ignored);
        harness.Lots.Should().BeEmpty();
    }

    [Fact]
    public async Task A_settlement_still_in_flight_is_retryable_and_raises_no_second_issuance()
    {
        var harness = new Harness
        {
            AllocationFailure = AZOAResult<AllocationResult>.FailureWithCode(
                "This allocation is still settling.", ConsumerSafeErrorCodes.SettlementPending),
        };

        var outcome = await harness.ApplyAsync();

        // Retryable, because the allocation ledger key makes the retry replay the ORIGINAL
        // issuance. A 2xx here would end the conversation with the units possibly unlanded.
        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.Lots.Single().Status.Should().Be(OnRampLotStatuses.Pending);
    }

    // ── Neither retrying forever nor giving up silently ──

    [Fact]
    public async Task An_account_whose_identity_check_has_lapsed_is_refused_and_left_visible()
    {
        var harness = new Harness { KycApproved = false };

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.AcceptedPending);
        harness.CapturedRequest.Should().BeNull();

        var lot = harness.Lots.Single();
        lot.Status.Should().Be(OnRampLotStatuses.Refused);
        lot.FiatMinorUnits.Should().Be(1000L, "the charge stays on the record so it can be refunded");
    }

    [Fact]
    public async Task A_refused_payment_is_not_re_attempted_on_every_redelivery()
    {
        var harness = new Harness { KycApproved = false };
        await harness.ApplyAsync();

        harness.KycApproved = true;   // even if the world changes, the decision stands until a person acts

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.AcceptedPending);
        harness.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task The_attempt_budget_parks_the_lot_instead_of_retrying_for_ever()
    {
        var harness = new Harness();
        harness.Options.MaxIssuanceAttempts = 2;
        harness.AllocationFailure = AZOAResult<AllocationResult>.Failure("The chain is unreachable.");

        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.Failed);
        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.Failed);

        // Third delivery: the budget is spent. The provider is told to stop, and the charge is
        // parked where an operator can see it -- not swallowed.
        (await harness.ApplyAsync()).Should().Be(FiatEventOutcome.AcceptedPending);

        var lot = harness.Lots.Single();
        lot.Status.Should().Be(OnRampLotStatuses.Stuck);
        lot.StatusReason.Should().Contain("attempts");
    }

    [Fact]
    public async Task A_payment_settled_in_another_currency_is_refused_rather_than_converted()
    {
        var harness = new Harness();
        harness.Session = MakeSession(currency: "eur");

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.AcceptedPending);
        harness.CapturedRequest.Should().BeNull();
        harness.Lots.Single().Status.Should().Be(OnRampLotStatuses.Refused);
    }

    [Fact]
    public async Task A_platform_unit_that_does_not_exist_yet_leaves_the_payment_retryable()
    {
        var harness = new Harness { PlatformTokenAvailable = false };

        var outcome = await harness.ApplyAsync();

        outcome.Should().Be(FiatEventOutcome.Failed);
        harness.Lots.Should().BeEmpty();
    }

    [Fact]
    public void The_on_ramp_handler_is_the_marker_the_live_boot_guard_looks_for()
    {
        // FiatRailStartup.Validate refuses to boot the live environment unless an
        // IFiatSettlementHandler exists. If this ever became a plain IFiatEventHandler, a node
        // could charge a real card with nothing able to credit it.
        typeof(IFiatSettlementHandler).IsAssignableFrom(typeof(OnRampSettlementHandler))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("checkout.session.completed")]
    [InlineData("checkout.session.async_payment_succeeded")]
    public void The_handler_claims_the_events_that_mean_money_actually_cleared(string eventType)
    {
        new Harness().BuildHandler().Handles(eventType).Should().BeTrue();
    }

    [Theory]
    [InlineData("charge.refunded")]
    [InlineData("checkout.session.expired")]
    [InlineData("charge.dispute.created")]
    public void The_handler_claims_nothing_that_is_not_a_cleared_payment(string eventType)
    {
        new Harness().BuildHandler().Handles(eventType).Should().BeFalse();
    }

    // ── The conversion itself ──

    [Fact]
    public void One_cent_buys_one_whole_unit_at_the_settled_base()
    {
        // 1 unit = $0.01 at 2 decimals => one cent buys 100 base units.
        PriceScheduleMath.TryConvert(1, SettledBaseRate, out var units, out _).Should().BeTrue();
        units.Should().Be(new BigInteger(100));
    }

    [Fact]
    public void A_fractional_result_is_floored_never_rounded_up()
    {
        // Rounding up would create units nobody paid for.
        PriceScheduleMath.TryConvert(3, 0.5m, out var units, out _).Should().BeTrue();
        units.Should().Be(new BigInteger(1));
    }

    [Fact]
    public void A_payment_that_converts_to_zero_units_is_refused()
    {
        PriceScheduleMath.TryConvert(1, 0.5m, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void A_payment_total_that_is_not_money_converts_to_nothing(long amountMinorUnits)
    {
        PriceScheduleMath.TryConvert(amountMinorUnits, SettledBaseRate, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("nonsense")]
    [InlineData("1e9")]
    public void A_price_that_is_not_a_positive_decimal_is_refused(string raw)
    {
        PriceScheduleMath.TryParseRate(raw, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_price_past_the_registration_cap_is_refused_as_a_misplaced_decimal_point()
    {
        var overCap = (PriceScheduleLimits.MaxUnitsPerMinorUnit + 1m).ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        PriceScheduleMath.TryParseRate(overCap, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void The_stamp_direction_is_the_reciprocal_of_the_registered_price()
    {
        // The D16 stamp is minor units per BASE unit: at 2 decimals, "1 unit = $0.01" is 0.01
        // cents per base unit, not 1. Getting this wrong is a silent 100x on a financial record.
        PriceScheduleMath.TryDeriveMinorUnitsPerBaseUnit(SettledBaseRate, out var rate).Should().BeTrue();
        rate.Should().Be(0.01m);
    }

    // ── Registration validation ──

    [Fact]
    public void A_price_in_a_currency_this_node_does_not_pin_is_refused_not_converted()
    {
        var refusal = PriceScheduleValidator.Validate(
            new PriceScheduleRegistration
            {
                ChainType = ChainType,
                AssetId = AssetId,
                CurrencyCode = "EUR",
                UnitsPerMinorUnit = "100",
                Source = "test",
            },
            out _);

        refusal.Should().NotBeNull();
    }

    [Fact]
    public void A_price_with_no_stated_provenance_is_refused()
    {
        var refusal = PriceScheduleValidator.Validate(
            new PriceScheduleRegistration
            {
                ChainType = ChainType,
                AssetId = AssetId,
                UnitsPerMinorUnit = "100",
                Source = "   ",
            },
            out _);

        refusal.Should().NotBeNull();
    }

    [Fact]
    public void A_price_with_no_asset_is_refused_because_it_prices_nothing()
    {
        var refusal = PriceScheduleValidator.Validate(
            new PriceScheduleRegistration
            {
                ChainType = ChainType,
                AssetId = "  ",
                UnitsPerMinorUnit = "100",
                Source = "test",
            },
            out _);

        refusal.Should().NotBeNull();
    }

    // ── The read side: the same price decides what the operation was worth (D16) ──

    [Fact]
    public async Task The_value_stamp_reads_the_same_registered_price_that_sold_the_units()
    {
        var store = new PriceScheduleDeclaredRateStore(
            FakeScheduleStore.With(Schedule(rate: SettledBaseRate, version: 3)),
            NeverDeclares.Instance,
            NullLogger<PriceScheduleDeclaredRateStore>.Instance);

        var declared = await store.TryGetLatestAsync(ChainType, AssetId);

        declared.Should().NotBeNull();
        declared!.MinorUnitsPerBaseUnit.Should().Be(0.01m);
        declared.Version.Should().Be("3");
        declared.CurrencyCode.Should().Be("USD");
    }

    [Fact]
    public async Task Two_scopes_pricing_one_asset_resolve_to_the_node_wide_price()
    {
        var store = new PriceScheduleDeclaredRateStore(
            FakeScheduleStore.With(
                Schedule(rate: 100m, version: 3),
                Schedule(scopeKey: TenantUserId.ToString("N"), rate: 250m, version: 1)),
            NeverDeclares.Instance,
            NullLogger<PriceScheduleDeclaredRateStore>.Instance);

        // OI-64. A scope-less read is a NODE-level read, so the operator's own price is the one
        // it can name; a tenant's override does not blind the node to its own schedule. The
        // genuinely unresolvable case (two tenant scopes, no node-wide price) is pinned in
        // Fiat/TenantPriceScheduleStoreTests.
        var resolved = await store.TryGetLatestAsync(ChainType, AssetId);
        resolved!.MinorUnitsPerBaseUnit.Should().Be(0.01m);
        resolved.Version.Should().Be("3");
    }

    [Fact]
    public async Task An_asset_with_no_registered_price_still_reaches_the_configured_declarations()
    {
        var fallback = new TenantDeclaredRate(
            ChainType, AssetId, "USD", 0.02m, Now.AddHours(-1), "configured");

        var store = new PriceScheduleDeclaredRateStore(
            FakeScheduleStore.With(),
            new AlwaysDeclares(fallback),
            NullLogger<PriceScheduleDeclaredRateStore>.Instance);

        (await store.TryGetLatestAsync(ChainType, AssetId)).Should().BeSameAs(fallback);
    }

    [Fact]
    public async Task A_retired_price_falls_through_to_the_configured_declarations_and_never_stamps_itself()
    {
        var store = new PriceScheduleDeclaredRateStore(
            FakeScheduleStore.With(Schedule(rate: 100m, version: 9, status: PriceScheduleStatuses.Retired)),
            NeverDeclares.Instance,
            NullLogger<PriceScheduleDeclaredRateStore>.Instance);

        (await store.TryGetLatestAsync(ChainType, AssetId)).Should().BeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>A cleared $10.00 test-mode payment for the funding intent under test.</summary>
    private static StripeCheckoutSession MakeSession(
        long amountTotalMinorUnits = 1000L,
        string status = "complete",
        string paymentStatus = "paid",
        string currency = "usd",
        bool livemode = false,
        string? clientReferenceId = null) =>
        new()
        {
            SessionId = SessionId,
            PaymentIntentId = PaymentIntentId,
            Status = status,
            PaymentStatus = paymentStatus,
            AmountTotalMinorUnits = amountTotalMinorUnits,
            Currency = currency,
            Livemode = livemode,
            ClientReferenceId = clientReferenceId ?? IntentId.ToString("N"),
        };

    private static RegisteredPriceSchedule Schedule(
        string? scopeKey = null,
        decimal rate = SettledBaseRate,
        long version = 1,
        string status = PriceScheduleStatuses.Active) =>
        new(
            RecordId: $"schedule-{version}",
            ScopeKey: scopeKey ?? PriceScheduleScope.Node,
            TenantId: null,
            ChainType: SurrealTenantPriceScheduleStore.NormalizeChain(ChainType),
            AssetId: AssetId,
            CurrencyCode: "USD",
            Version: version,
            UnitsPerMinorUnit: rate,
            Source: "test-treasury",
            Note: null,
            Status: status,
            DeclaredAt: Now.AddHours(-1),
            RegisteredAt: Now.AddHours(-1));

    /// <summary>An in-memory lot ledger honouring the same compare-and-swap contract as the real one.</summary>
    private sealed class FakeLotLedger : IOnRampLotLedger
    {
        private readonly Dictionary<string, OnRampLot> _rows = new(StringComparer.Ordinal);

        public IReadOnlyCollection<OnRampLot> Rows => _rows.Values;

        public Task<AZOAResult<OnRampLotClaim>> ClaimAsync(OnRampLot candidate, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(candidate.Id, out var existing))
                return Task.FromResult(AZOAResult<OnRampLotClaim>.Success(
                    new OnRampLotClaim(Clone(existing), Created: false)));

            _rows[candidate.Id] = Clone(candidate);
            return Task.FromResult(AZOAResult<OnRampLotClaim>.Success(
                new OnRampLotClaim(Clone(candidate), Created: true)));
        }

        public Task<AZOAResult<OnRampLot?>> GetByIntentAsync(string intentId, CancellationToken ct = default)
        {
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

        public Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListIssuedForUserAsync(
            Guid userId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                _rows.Values
                    .Where(row => row.SubjectUserId == userId.ToString("N")
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

        private static OnRampLot Clone(OnRampLot row) => System.Text.Json.JsonSerializer.Deserialize<OnRampLot>(System.Text.Json.JsonSerializer.Serialize(row))!;
    }

    /// <summary>A price-schedule store backed by a fixed set of current versions.</summary>
    private sealed class FakeScheduleStore : ITenantPriceScheduleStore
    {
        private readonly Dictionary<string, RegisteredPriceSchedule> _byScope = new(StringComparer.Ordinal);

        public static FakeScheduleStore With(params RegisteredPriceSchedule[] schedules)
        {
            var store = new FakeScheduleStore();
            foreach (var schedule in schedules)
                store._byScope[schedule.ScopeKey] = schedule;
            return store;
        }

        public Task<AZOAResult<RegisteredPriceSchedule>> RegisterAsync(
            PriceScheduleRegistration registration,
            string scopeKey,
            Guid? tenantId,
            Guid registeredByUserId,
            CancellationToken ct = default)
            => throw new NotSupportedException("The fake store is read-only.");

        public Task<AZOAResult<RegisteredPriceSchedule?>> GetLatestAsync(
            string scopeKey, string chainType, string assetId, CancellationToken ct = default)
        {
            _byScope.TryGetValue(scopeKey, out var schedule);
            return Task.FromResult(new AZOAResult<RegisteredPriceSchedule?> { Result = schedule });
        }

        public Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListVersionsAsync(
            string scopeKey, string chainType, string assetId, int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                _byScope.TryGetValue(scopeKey, out var schedule)
                    ? new List<RegisteredPriceSchedule> { schedule }
                    : new List<RegisteredPriceSchedule>()));

        public Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListLatestPerScopeAsync(
            string chainType, string assetId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                _byScope.Values.ToList()));
    }

    /// <summary>A declared-rate store that never quotes; the honest "nothing is declared" state.</summary>
    private sealed class NeverDeclares : ITenantDeclaredRateStore
    {
        public static NeverDeclares Instance { get; } = new();

        public ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
            string chainType, string assetId, CancellationToken ct = default)
            => ValueTask.FromResult<TenantDeclaredRate?>(null);
    }

    /// <summary>A declared-rate store standing in for the configured declarations.</summary>
    private sealed class AlwaysDeclares : ITenantDeclaredRateStore
    {
        private readonly TenantDeclaredRate _rate;

        public AlwaysDeclares(TenantDeclaredRate rate) => _rate = rate;

        public ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
            string chainType, string assetId, CancellationToken ct = default)
            => ValueTask.FromResult<TenantDeclaredRate?>(_rate);
    }

    /// <summary>Builds a handler over fakes, with one knob per behaviour under test.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            Session = MakeSession();
            Schedules[PriceScheduleScope.Node] = OnRampSagaTests.Schedule();
        }

        public StripeCheckoutSession Session { get; set; }

        public Dictionary<string, RegisteredPriceSchedule> Schedules { get; } = new(StringComparer.Ordinal);

        public FakeLotLedger Ledger { get; } = new();

        public OnRampOptions Options { get; } = new();

        public Guid? DeliveryIntentId { get; init; } = IntentId;

        public Guid? MetadataSubjectUserId { get; init; }

        public Guid? IntentTenantId { get; init; }

        public bool KycApproved { get; set; } = true;

        public bool RailResolvable { get; init; } = true;

        public bool PlatformTokenAvailable { get; init; } = true;

        public int ChargebackWindowDays { get; init; } = 120;

        public string NetAmount { get; init; } = "100000";

        public string NodeFeeAmount { get; init; } = "0";

        public AZOAResult<AllocationResult>? AllocationFailure { get; set; }

        public Guid IssuanceId { get; } = Guid.NewGuid();

        public string IssuanceStatus { get; set; } = OperationStatus.Completed;

        public string IssuanceTransactionId { get; set; } = "CONFIRMED_ISSUANCE_TX";

        public AllocationRequest? CapturedRequest { get; set; }

        public string? CapturedIdempotencyKey { get; private set; }

        /// <summary>On whose authority the rail asked the reserve to be drawn (OI-21).</summary>
        public ReserveDrawAuthority? CapturedDrawAuthority { get; private set; }

        public IReadOnlyCollection<OnRampLot> Lots => Ledger.Rows;

        public Task<FiatEventOutcome> ApplyAsync() => BuildHandler().ApplyAsync(BuildContext());

        public OnRampSettlementHandler BuildHandler()
        {
            var fiat = new FiatOptions
            {
                Enabled = true,
                Environment = RailEnvironmentNames.Test,
                ChargebackWindowDays = ChargebackWindowDays,
                Test = { IsEnabled = true, SecretKey = "sk_test_x", WebhookSecret = "whsec_x" },
            };
            fiat.Checkout.Currency = "usd";

            var gateway = new Mock<IStripeGateway>();
            gateway
                .Setup(g => g.GetCheckoutSessionAsync(
                    It.IsAny<StripeCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeCheckoutSession>.Success(Session));

            var sessions = new Mock<IHostedSessionStore>();
            sessions
                .Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<HostedSession>.Success(new HostedSession
                {
                    Id = IntentId.ToString("N"),
                    FlowKind = HostedSessionFlowKinds.FiatCheckout,
                    ProviderKey = FiatRailIds.StripeProviderKey,
                    SubjectUserId = SubjectUserId.ToString("N"),
                    TenantId = IntentTenantId?.ToString("N"),
                    State = HostedSessionStates.Completed,
                    StateVersion = 1,
                }));

            var platformToken = new Mock<IPlatformTokenGenesis>();
            platformToken
                .Setup(p => p.GetStateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => PlatformTokenAvailable
                    ? AZOAResult<PlatformTokenState>.Success(new PlatformTokenState(
                        new PlatformTokenDefinition(
                            ChainType, "Devnet", AssetId, "Arda", UnitName, 2,
                            new BigInteger(1_000_000_000), "RESERVEADDR", Now.AddDays(-1)),
                        new PlatformTokenSupply(
                            new BigInteger(1_000_000_000), BigInteger.Zero, BigInteger.Zero,
                            new BigInteger(1_000_000_000))))
                    : AZOAResult<PlatformTokenState>.FailureWithCode(
                        "No platform unit exists on this network.",
                        PlatformTokenGenesisCodes.NotGenesised));

            var allocations = new Mock<IAllocationManager>();
            allocations
                .Setup(a => a.AllocateAsync(
                    It.IsAny<Guid>(), It.IsAny<AllocationRequest>(), It.IsAny<Guid>(),
                    It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<Guid?>(),
                    It.IsAny<ReserveDrawAuthority?>()))
                .Returns((
                    Guid _, AllocationRequest request, Guid _, string? key, string _, Guid? _,
                    ReserveDrawAuthority? authority) =>
                {
                    CapturedRequest = request;
                    CapturedIdempotencyKey = key;
                    CapturedDrawAuthority = authority;

                    if (AllocationFailure is { } failure)
                        return Task.FromResult(failure);

                    return Task.FromResult(AZOAResult<AllocationResult>.Success(new AllocationResult
                    {
                        UserId = SubjectUserId,
                        WalletId = Guid.NewGuid(),
                        WalletAddress = "RECIPIENTADDR",
                        OperationId = IssuanceId,
                        GrossAmount = request.Amount,
                        NetAmount = NetAmount,
                        NodeFeeAmount = NodeFeeAmount,
                        NodeFeeScheduleVersion = 1,
                    }));
                });

            var operations = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
            operations.Setup(store => store.GetByIdAsync(IssuanceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var operation = new Mock<IBlockchainOperation>();
                    operation.SetupGet(row => row.Id).Returns(IssuanceId);
                    operation.SetupGet(row => row.Status).Returns(IssuanceStatus);
                    operation.SetupGet(row => row.Parameters).Returns(new Dictionary<string, string>
                    {
                        ["TxHash"] = IssuanceTransactionId,
                    });
                    return AZOAResult<IBlockchainOperation>.Success(operation.Object);
                });

            var kyc = new Mock<IRealValueKycGate>();
            kyc.Setup(g => g.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => KycApproved
                    ? AZOAResult<bool>.Success(true)
                    : AZOAResult<bool>.Failure("Identity verification is not current."));
            kyc.Setup(g => g.RequireCurrentApprovalAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => KycApproved
                    ? AZOAResult<bool>.Success(true)
                    : AZOAResult<bool>.Failure("Identity verification is not current."));

            var rail = new Mock<IValueRailProvider>();
            rail.SetupGet(r => r.SettlementProfile).Returns(
                SettlementProfile.ReversibleFor(TimeSpan.FromDays(ChargebackWindowDays), "the card payment clears"));

            var rails = new Mock<IValueRailProviderFactory>();
            IValueRailProvider? resolved = RailResolvable ? rail.Object : null;
            rails
                .Setup(f => f.TryGetRailProvider(
                    It.IsAny<string>(), It.IsAny<RailEnvironment>(), out resolved))
                .Returns(RailResolvable);

            return new OnRampSettlementHandler(
                fiat,
                Options,
                gateway.Object,
                sessions.Object,
                FakeScheduleStore.With(Schedules.Values.ToArray()),
                Ledger,
                platformToken.Object,
                allocations.Object,
                kyc.Object,
                rails.Object,
                NullLogger<OnRampSettlementHandler>.Instance,
                new FixedClock(Now),
                operations.Object);
        }

        private FiatEventContext BuildContext() => new(
            new FiatEvent
            {
                Id = "event-row",
                Provider = FiatRailIds.StripeProviderKey,
                ProviderEventId = "evt_1",
                EventType = "checkout.session.completed",
                Environment = RailEnvironmentNames.Test,
                Status = FiatEventStatuses.Received,
                PayloadDigest = "digest",
            },
            "checkout.session.completed",
            SessionId,
            PaymentIntentId,
            ChargeId,
            DeliveryIntentId,
            MetadataSubjectUserId,
            RailEnvironment.Test);
    }

    /// <summary>A clock that does not move, so a window assertion is exact.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
