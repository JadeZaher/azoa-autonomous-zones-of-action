// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
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
/// A resent idempotency key used to collapse every same-key situation into one indistinguishable
/// 409 (plain <see cref="AzoaErrorCodes.Conflict"/>). These drive each situation through
/// <see cref="FiatCheckoutController"/> backed by the real <see cref="FiatRailProvider"/> and
/// in-memory hosted-session store — only Stripe HTTP is faked — and assert the specific wire code a
/// client now gets to tell "finished" from "still in flight" from "needs a new payment", with the
/// same 409 and the same human message as before.
/// </summary>
public sealed class FiatCheckoutSameKeyReplayTests
{
    [Fact]
    public async Task Each_same_key_situation_answers_409_with_its_own_code_and_unchanged_message()
    {
        var harness = new Harness();

        // REQUEST_KEY_IN_USE: a second account replaying the FIRST account's literal admission
        // key. The controller always folds the caller's own subject into the key it builds, so
        // this collision can only be produced by calling the rail directly with an explicit
        // shared key — exactly what a caller who ignored namespacing (or a bug upstream) would
        // send. The other three situations below are single-caller replays and go through the
        // full controller, which exercises the same status-mapping switch this code shares.
        var owner = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        const string sharedKey = "shared-admission-key";
        (await harness.Rail.BeginCheckoutAsync(harness.Request(owner, sharedKey))).IsError.Should().BeFalse();
        var keyInUse = await harness.Rail.BeginCheckoutAsync(harness.Request(intruder, sharedKey));
        keyInUse.IsError.Should().BeTrue();
        keyInUse.Code.Should().Be(FiatConsumerErrors.RequestKeyInUseCode);
        keyInUse.Message.Should().Be("That request key is already in use.");

        // INTENT_FINISHED: the claim this key names already reached a terminal state.
        var finishedSubject = Guid.NewGuid();
        var opened = await harness.StartAsync(finishedSubject, "finished-key");
        opened.Result.Should().BeOfType<OkObjectResult>();
        var openedIntent = ((AZOAResult<FiatCheckoutIntent>)((OkObjectResult)opened.Result!).Value!).Result!;
        await harness.CompleteAsync(openedIntent.IntentId);
        var finished = await harness.StartAsync(finishedSubject, "finished-key");
        AssertConflict(finished, FiatConsumerErrors.IntentFinishedCode, "This payment is already finished.");

        // INTENT_EXPIRED: the claim's hosted page outlived its pinned lifetime (D8), so
        // FiatHostedCreation reports the claim Attached with nothing left it can replay.
        var expiredSubject = Guid.NewGuid();
        (await harness.StartAsync(expiredSubject, "expired-key")).Result.Should().BeOfType<OkObjectResult>();
        harness.Clock.Advance(TimeSpan.FromMinutes(40));
        var expired = await harness.StartAsync(expiredSubject, "expired-key");
        AssertConflict(expired, FiatConsumerErrors.IntentExpiredCode, "This payment page has expired. Start a new payment.");

        // INTENT_NEEDS_REVIEW: the creation fence's 23-hour replay window elapsed with no
        // provider session ever attached; only an operator can reconcile from here.
        var reviewSubject = Guid.NewGuid();
        harness.Stripe.LoseNextCreateResponse = true;
        var lost = await harness.StartAsync(reviewSubject, "review-key");
        lost.Result.Should().NotBeOfType<OkObjectResult>("the lost create response must not look like a success");
        harness.Clock.Advance(TimeSpan.FromHours(24));
        var needsReview = await harness.StartAsync(reviewSubject, "review-key");
        AssertConflict(needsReview, FiatConsumerErrors.IntentNeedsReviewCode, "This payment needs a manual check before it can continue.");
    }

    [Fact]
    public async Task A_failed_provider_reread_fails_the_read_instead_of_reporting_not_started()
    {
        var harness = new Harness();
        var subject = Guid.NewGuid();

        var opened = await harness.Rail.BeginCheckoutAsync(harness.Request(subject, "reread-key"));
        opened.IsError.Should().BeFalse(opened.Message);

        // Stripe is reachable for creation but the re-read this GET depends on fails: exactly the
        // "may already be a paid, closed intent" situation FIX 2 covers.
        harness.Stripe.FailNextGet = true;
        var read = await harness.Controller(subject).Get(subject, opened.Result!.IntentId);

        read.Result.Should().BeOfType<ObjectResult>(
            "a read that cannot confirm settlement state must fail outright, never answer 200 with an invented not_started");
        var failure = (ObjectResult)read.Result!;
        failure.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var body = failure.Value.Should().BeOfType<AZOAResult<FiatCheckoutIntent>>().Subject;
        body.IsError.Should().BeTrue();
        body.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        body.Result.Should().BeNull("a failed read must not hand back any settlement state, invented or otherwise");
    }

    private static void AssertConflict(
        ActionResult<AZOAResult<FiatCheckoutIntent>> result, string code, string message)
    {
        result.Result.Should().BeOfType<ConflictObjectResult>();
        var conflict = (ConflictObjectResult)result.Result!;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var body = conflict.Value.Should().BeOfType<AZOAResult<FiatCheckoutIntent>>().Subject;
        body.IsError.Should().BeTrue();
        body.Code.Should().Be(code);
        body.Message.Should().Be(message);
    }

    private sealed class Harness
    {
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        public ReplayStripe Stripe { get; } = new();
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

            var kyc = new Mock<IRealValueKycGate>();
            kyc.Setup(gate => gate.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<bool>.Success(true));
            _store = new InMemoryHostedSessionStore();
            var services = new ServiceCollection()
                .AddSingleton<IHostedSessionStore>(_store)
                .AddSingleton(kyc.Object)
                .BuildServiceProvider();

            Rail = new FiatRailProvider(_options, Stripe, services.GetRequiredService<IServiceScopeFactory>(),
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

        /// <summary>Drives a claim straight to a terminal state, as a settlement handler would, without a real webhook.</summary>
        public async Task CompleteAsync(Guid intentId)
        {
            var row = (await _store.GetByIdAsync(intentId)).Result!;
            var transitioned = await _store.TryTransitionAsync(
                intentId, row.StateVersion, HostedSessionStates.Completed, null, null, null);
            transitioned.IsError.Should().BeFalse();
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
    /// Stripe's idempotency contract (same key + same parameters -> same object) plus two
    /// one-shot failure toggles: a lost create response, and a re-read that cannot be confirmed.
    /// </summary>
    private sealed class ReplayStripe : IStripeGateway
    {
        private readonly Dictionary<string, (string Fingerprint, StripeCheckoutSession Session)> _byKey = new();
        private readonly object _gate = new();
        private int _sessionsCreated;

        public bool LoseNextCreateResponse { get; set; }
        public bool FailNextGet { get; set; }

        public Task<AZOAResult<StripeCheckoutSession>> CreateCheckoutSessionAsync(
            StripeCredentials credentials, StripeCheckoutSessionRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var fingerprint = $"{request.IntentId}|{request.AmountMinorUnits}|{request.Currency}|{request.ExpiresAt:O}|{request.SuccessUrl}|{request.CancelUrl}";
                if (_byKey.TryGetValue(request.IdempotencyKey, out var existing))
                {
                    return Task.FromResult(existing.Fingerprint == fingerprint
                        ? AZOAResult<StripeCheckoutSession>.Success(existing.Session)
                        : AZOAResult<StripeCheckoutSession>.Failure("idempotency_error: parameters differ for this key"));
                }

                _sessionsCreated++;
                var session = new StripeCheckoutSession
                {
                    SessionId = $"cs_test_{_sessionsCreated}",
                    Url = $"https://checkout.stripe.example/cs_test_{_sessionsCreated}",
                    Status = "open",
                    PaymentStatus = "unpaid",
                    ExpiresAt = request.ExpiresAt,
                    ClientReferenceId = request.IntentId,
                };
                _byKey[request.IdempotencyKey] = (fingerprint, session);

                if (LoseNextCreateResponse)
                {
                    LoseNextCreateResponse = false;
                    return Task.FromResult(AZOAResult<StripeCheckoutSession>.Failure("The response was lost."));
                }

                return Task.FromResult(AZOAResult<StripeCheckoutSession>.Success(session));
            }
        }

        public Task<AZOAResult<StripeCheckoutSession>> GetCheckoutSessionAsync(
            StripeCredentials credentials, string sessionId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (FailNextGet)
                {
                    FailNextGet = false;
                    return Task.FromResult(AZOAResult<StripeCheckoutSession>.Failure("Stripe could not be reached."));
                }

                var found = _byKey.Values.Select(entry => entry.Session).FirstOrDefault(session => session.SessionId == sessionId);
                return Task.FromResult(found is null
                    ? AZOAResult<StripeCheckoutSession>.Failure("No such session.")
                    : AZOAResult<StripeCheckoutSession>.Success(found));
            }
        }
    }
}
