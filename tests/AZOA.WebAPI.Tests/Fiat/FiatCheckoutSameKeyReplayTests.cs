// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// The same-key resend contract for checkout begin (Providers/Fiat/AGENTS.md §same-key-replay-codes),
/// driven through <see cref="FiatCheckoutController"/>, the real <see cref="FiatRailProvider"/>, the
/// real <see cref="StripeGateway"/> and the in-memory hosted-session store. Only Stripe's HTTP is fake.
/// </summary>
public sealed class FiatCheckoutSameKeyReplayTests
{
    private static readonly string[] FinalCodes =
    {
        FiatConsumerErrors.RequestKeyInUseCode, FiatConsumerErrors.IntentFinishedCode,
        FiatConsumerErrors.IntentExpiredCode, FiatConsumerErrors.IntentNeedsReviewCode,
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
        bool ExpectPaymentUrl = false);

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
        ["intent completed"] = new("complete", "paid", HostedSessionStates.Completed, WithinLifetime, false,
            409, FiatConsumerErrors.IntentFinishedCode),
        ["Stripe expired the unpaid session"] = new("expired", "unpaid", null, PastExpiry, false,
            409, FiatConsumerErrors.IntentExpiredCode),
        ["Stripe still reads open but unpaid, page past expiry"] = new("open", "unpaid", null, PastExpiry, false,
            409, FiatConsumerErrors.IntentExpiredCode),
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
        var harness = new Harness();
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

        var (http, body) = Unwrap(await harness.StartAsync(subject, "resend-key"));

        http.Should().Be(situation.ExpectedHttp, row);
        body.Code.Should().Be(situation.ExpectedCode, row);
        harness.Stripe.SessionsCreated.Should().Be(1, "a resend never opens a second payment page");
        if (situation.ExpectedHttp != 200)
        {
            body.Result.Should().BeNull();
            return;
        }

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
    }

    [Fact]
    public async Task Stripe_idempotency_key_in_use_is_a_non_final_409_and_the_retry_resumes_the_same_page()
    {
        var harness = new Harness();
        var subject = Guid.NewGuid();
        harness.Stripe.NextCreateCollidesWithInFlightRequest = true;

        var collided = await harness.StartAsync(subject, "in-flight-key");

        collided.Result.Should().BeOfType<ConflictObjectResult>("a same-key request still in flight is not a refusal");
        var (http, body) = Unwrap(collided);
        http.Should().Be(StatusCodes.Status409Conflict);
        body.Code.Should().Be(AzoaErrorCodes.Conflict);
        FinalCodes.Should().NotContain(body.Code, "clients release the key on a final code");

        var retried = Unwrap(await harness.StartAsync(subject, "in-flight-key"));
        retried.Http.Should().Be(200);
        retried.Body.Result!.PaymentUrl.Should().Be(harness.Stripe.UrlOf(retried.Body.Result.IntentId));
        harness.Stripe.SessionsCreated.Should().Be(1, "keeping the key let the retry find the in-flight request's page");
    }

    [Fact]
    public async Task Another_accounts_key_and_an_unreplayable_create_answer_their_own_409_codes()
    {
        var harness = new Harness();

        // REQUEST_KEY_IN_USE: only reachable on the rail with an explicit shared admission key; the
        // controller always folds the caller's own subject into the key it builds.
        const string sharedKey = "shared-admission-key";
        (await harness.Rail.BeginCheckoutAsync(harness.Request(Guid.NewGuid(), sharedKey))).IsError.Should().BeFalse();
        var keyInUse = await harness.Rail.BeginCheckoutAsync(harness.Request(Guid.NewGuid(), sharedKey));
        keyInUse.Code.Should().Be(FiatConsumerErrors.RequestKeyInUseCode);
        keyInUse.Message.Should().Be("That request key is already in use.");

        // INTENT_NEEDS_REVIEW: the 23-hour replay window elapsed with no provider session attached.
        var reviewSubject = Guid.NewGuid();
        harness.Stripe.LoseNextCreateResponse = true;
        Unwrap(await harness.StartAsync(reviewSubject, "review-key")).Http.Should().Be(503);
        harness.Clock.Advance(TimeSpan.FromHours(24));
        var (http, body) = Unwrap(await harness.StartAsync(reviewSubject, "review-key"));
        http.Should().Be(409);
        body.Code.Should().Be(FiatConsumerErrors.IntentNeedsReviewCode);
        body.Message.Should().Be("This payment needs a manual check before it can continue.");
    }

    [Fact]
    public async Task A_failed_provider_reread_fails_the_read_instead_of_reporting_not_started()
    {
        var harness = new Harness();
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
    {
        var answer = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        return (answer.StatusCode ?? 200, answer.Value.Should().BeOfType<AZOAResult<FiatCheckoutIntent>>().Subject);
    }

    private sealed class Harness
    {
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        public FakeStripeHttp Stripe { get; }
        public FiatRailProvider Rail { get; }
        private readonly FiatOptions _options;
        private readonly IValueRailProviderFactory _factory;
        private readonly InMemoryHostedSessionStore _store;

        public Harness()
        {
            _options = new FiatOptions { Enabled = true, Environment = RailEnvironmentNames.Test, ApiBaseUrl = "https://api.example" };
            _options.Test.IsEnabled = true;
            _options.Test.SecretKey = "sk_test_x";
            _options.Test.WebhookSecret = "whsec_x";
            _options.Checkout.AllowedReturnOrigins.Add("https://app.example");
            _options.Checkout.AllowedPurposes.Add(FiatCheckoutPurposes.FundsTopUp);
            _options.Checkout.DefaultSuccessUrl = "https://app.example/funds/done";
            _options.Checkout.DefaultCancelUrl = "https://app.example/funds";

            Stripe = new FakeStripeHttp();
            var httpClients = new Mock<IHttpClientFactory>();
            httpClients.Setup(factory => factory.CreateClient(StripeGateway.HttpClientName)).Returns(new HttpClient(Stripe));
            var gateway = new StripeGateway(httpClients.Object, NullLogger<StripeGateway>.Instance);

            var kyc = new Mock<IRealValueKycGate>();
            kyc.Setup(gate => gate.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<bool>.Success(true));
            _store = new InMemoryHostedSessionStore();
            var services = new ServiceCollection()
                .AddSingleton<IHostedSessionStore>(_store)
                .AddSingleton(kyc.Object)
                .BuildServiceProvider();

            Rail = new FiatRailProvider(_options, gateway, services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FiatRailProvider>.Instance, Clock);
            _factory = new SingleRailFactory(Rail);
        }

        /// <summary>An explicit, un-namespaced admission key — what the rail sees once the controller (or an attacker) has built one.</summary>
        public FiatCheckoutRequest Request(Guid subject, string admissionKey) => new()
        {
            SubjectUserId = subject,
            AdmissionKey = admissionKey,
            AmountMinorUnits = 1_000,
        };

        public FiatCheckoutController Controller(Guid subject) => new(_factory, _options)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("UserId", subject.ToString()) }, "Test")),
                },
            },
        };

        public Task<ActionResult<AZOAResult<FiatCheckoutIntent>>> StartAsync(Guid subject, string requestKey) =>
            Controller(subject).Start(subject, new FiatCheckoutStartRequest { RequestKey = requestKey, AmountMinorUnits = 1_000 });

        /// <summary>Moves the claim as the webhook inbox / settlement handler would, without a real delivery.</summary>
        public async Task TransitionAsync(Guid intentId, string state)
        {
            var row = (await _store.GetByIdAsync(intentId)).Result!;
            var transitioned = await _store.TryTransitionAsync(intentId, row.StateVersion, state, null, null, null);
            transitioned.Result.Should().NotBeNull();
        }
    }

    private sealed class SingleRailFactory(IValueRailProvider provider) : IValueRailProviderFactory
    {
        public IValueRailProvider GetRailProvider(string railId, RailEnvironment environment) => provider;

        public bool TryGetRailProvider(
            string railId, RailEnvironment environment, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IValueRailProvider? result)
        {
            result = provider;
            return true;
        }

        public IReadOnlyCollection<string> RegisteredRailIds => new[] { FiatRailIds.UsdRail };
        public string DefaultRailId => FiatRailIds.UsdRail;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Stripe's Checkout Sessions HTTP surface: same idempotency key + same form → the same session,
    /// plus scripted faults (a lost create response, an unreachable re-read, a same-key request
    /// still in flight) and a way to move a session the way a payer's actions at Stripe would.
    /// </summary>
    private sealed class FakeStripeHttp : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Form, StripeSessionState Session)> _byKey = new();
        private readonly object _gate = new();

        public int SessionsCreated { get; private set; }
        public bool LoseNextCreateResponse { get; set; }
        public bool FailNextGet { get; set; }
        public bool NextCreateCollidesWithInFlightRequest { get; set; }

        /// <summary>Moves the session Stripe holds for <paramref name="intentId"/>, as the payer's actions there would.</summary>
        public void Settle(Guid intentId, string status, string paymentStatus)
        {
            lock (_gate)
            {
                var session = SessionFor(intentId);
                session.Status = status;
                session.PaymentStatus = paymentStatus;
            }
        }

        public string UrlOf(Guid intentId)
        {
            lock (_gate)
                return SessionFor(intentId).Url;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/checkout/sessions")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : string.Empty;
                return Create(key, form);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal))
                return Read(Uri.UnescapeDataString(path["/v1/checkout/sessions/".Length..]));

            return Error(HttpStatusCode.NotFound, "resource_missing");
        }

        private HttpResponseMessage Create(string idempotencyKey, string form)
        {
            lock (_gate)
            {
                if (_byKey.TryGetValue(idempotencyKey, out var existing))
                    return existing.Form == form
                        ? Json(HttpStatusCode.OK, existing.Session.ToJson())
                        : Error(HttpStatusCode.BadRequest, "idempotency_error");

                SessionsCreated++;
                var fields = ParseForm(form);
                var session = new StripeSessionState
                {
                    Id = $"cs_test_{SessionsCreated}",
                    Url = $"https://checkout.stripe.example/cs_test_{SessionsCreated}",
                    AmountTotal = long.Parse(fields["line_items[0][price_data][unit_amount]"]),
                    Currency = fields["line_items[0][price_data][currency]"],
                    ExpiresAt = fields.TryGetValue("expires_at", out var expires) ? long.Parse(expires) : null,
                    ClientReferenceId = fields["client_reference_id"],
                };
                _byKey[idempotencyKey] = (form, session);

                if (NextCreateCollidesWithInFlightRequest)
                {
                    // Another request holding this key is still running at Stripe; it will finish
                    // and own the session, but THIS request is told the key is busy.
                    NextCreateCollidesWithInFlightRequest = false;
                    return Error(HttpStatusCode.Conflict, StripeGateway.IdempotencyKeyInUseErrorCode);
                }

                if (LoseNextCreateResponse)
                {
                    LoseNextCreateResponse = false;
                    throw new HttpRequestException("The connection was reset after Stripe created the session.");
                }

                return Json(HttpStatusCode.OK, session.ToJson());
            }
        }

        private HttpResponseMessage Read(string sessionId)
        {
            lock (_gate)
            {
                if (FailNextGet)
                {
                    FailNextGet = false;
                    throw new HttpRequestException("Stripe could not be reached.");
                }

                var found = _byKey.Values.Select(entry => entry.Session).FirstOrDefault(session => session.Id == sessionId);
                return found is null ? Error(HttpStatusCode.NotFound, "resource_missing") : Json(HttpStatusCode.OK, found.ToJson());
            }
        }

        private StripeSessionState SessionFor(Guid intentId) =>
            _byKey.Values.Select(entry => entry.Session).Single(session => session.ClientReferenceId == intentId.ToString("N"));

        private static Dictionary<string, string> ParseForm(string form) => form
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty);

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error(HttpStatusCode status, string code) => Json(status, JsonSerializer.Serialize(new
        {
            error = new { code, type = "invalid_request_error", message = "Stripe-side detail that must never reach a consumer." },
        }));
    }

    private sealed class StripeSessionState
    {
        public string Id { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string Status { get; set; } = "open";
        public string PaymentStatus { get; set; } = "unpaid";
        public long AmountTotal { get; init; }
        public string Currency { get; init; } = "usd";
        public long? ExpiresAt { get; init; }
        public string ClientReferenceId { get; init; } = string.Empty;

        public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["object"] = "checkout.session",
            ["url"] = Status == "open" ? Url : null,
            ["status"] = Status,
            ["payment_status"] = PaymentStatus,
            ["amount_total"] = AmountTotal,
            ["currency"] = Currency,
            ["livemode"] = false,
            ["expires_at"] = ExpiresAt,
            ["client_reference_id"] = ClientReferenceId,
        });
    }
}
