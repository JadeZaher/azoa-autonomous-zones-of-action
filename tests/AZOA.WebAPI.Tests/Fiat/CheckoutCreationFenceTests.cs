// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// D1 + D8 through <see cref="FiatRailProvider.BeginCheckoutAsync"/> with the real in-memory
/// hosted-session store and a Stripe fake that enforces idempotency-key semantics.
/// </summary>
public sealed class CheckoutCreationFenceTests
{
    private static readonly Guid Subject = Guid.Parse("11111111111111111111111111111111");

    [Fact]
    public async Task A_lost_create_response_replayed_minutes_later_returns_the_same_stripe_session()
    {
        var harness = new Harness();
        harness.Stripe.LoseNextResponse = true;
        (await harness.BeginAsync()).IsError.Should().BeTrue();

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var replay = await harness.BeginAsync();

        replay.IsError.Should().BeFalse(replay.Message);
        harness.Stripe.SessionsCreated.Should().Be(1, "the replay reuses the claim key with identical parameters");
        replay.Result!.PaymentUrl.Should().Be(harness.Stripe.LastUrl);
    }

    [Fact]
    public async Task A_replay_after_the_pinned_lifetime_runs_short_asks_for_a_new_payment_instead_of_looping()
    {
        var harness = new Harness();
        harness.Stripe.LoseNextResponse = true;
        (await harness.BeginAsync()).IsError.Should().BeTrue();

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        var replay = await harness.BeginAsync();

        replay.Code.Should().Be(FiatConsumerErrors.IntentExpiredCode);
        harness.Stripe.CreateCalls.Should().Be(1, "a create Stripe would refuse is never sent");
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_open_exactly_one_stripe_session()
    {
        var harness = new Harness();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => harness.BeginAsync()));

        harness.Stripe.SessionsCreated.Should().Be(1);
        results.Where(result => !result.IsError).Select(result => result.Result!.PaymentUrl)
            .Distinct().Should().ContainSingle();
    }

    private sealed class Harness
    {
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        public IdempotentStripe Stripe { get; } = new();
        private readonly FiatRailProvider _rail;

        public Harness()
        {
            var options = new FiatOptions { Enabled = true, Environment = RailEnvironmentNames.Test, ApiBaseUrl = "https://api.example" };
            options.Test.IsEnabled = true;
            options.Test.SecretKey = "sk_test_x";
            options.Test.WebhookSecret = "whsec_x";
            options.Checkout.AllowedReturnOrigins.Add("https://app.example");
            options.Checkout.AllowedPurposes.Add(FiatCheckoutPurposes.FundsTopUp);
            options.Checkout.DefaultSuccessUrl = "https://app.example/funds/done";
            options.Checkout.DefaultCancelUrl = "https://app.example/funds";

            var kyc = new Mock<IRealValueKycGate>();
            kyc.Setup(gate => gate.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<bool>.Success(true));
            var services = new ServiceCollection()
                .AddSingleton<IHostedSessionStore>(new InMemoryHostedSessionStore())
                .AddSingleton(kyc.Object)
                .BuildServiceProvider();

            _rail = new FiatRailProvider(options, Stripe, services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FiatRailProvider>.Instance, Clock);
        }

        public Task<AZOAResult<FiatCheckoutIntent>> BeginAsync() => _rail.BeginCheckoutAsync(new FiatCheckoutRequest
        {
            SubjectUserId = Subject,
            AdmissionKey = $"{Subject:N}:self:top-up-1",
            AmountMinorUnits = 1_000,
        });
    }

    /// <summary>Stripe's idempotency contract: same key + same parameters → same object; changed parameters → error.</summary>
    private sealed class IdempotentStripe : IStripeGateway
    {
        private readonly Dictionary<string, (string Fingerprint, StripeCheckoutSession Session)> _byKey = new();
        private readonly object _gate = new();

        public bool LoseNextResponse { get; set; }
        public int CreateCalls { get; private set; }
        public int SessionsCreated { get; private set; }
        public string? LastUrl { get; private set; }

        public Task<AZOAResult<StripeCheckoutSession>> CreateCheckoutSessionAsync(
            StripeCredentials credentials, StripeCheckoutSessionRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                CreateCalls++;
                var fingerprint = $"{request.IntentId}|{request.AmountMinorUnits}|{request.Currency}|{request.ExpiresAt:O}|{request.SuccessUrl}|{request.CancelUrl}";
                if (_byKey.TryGetValue(request.IdempotencyKey, out var existing))
                {
                    return Task.FromResult(existing.Fingerprint == fingerprint
                        ? AZOAResult<StripeCheckoutSession>.Success(existing.Session)
                        : AZOAResult<StripeCheckoutSession>.Failure("idempotency_error: parameters differ for this key"));
                }

                SessionsCreated++;
                var session = new StripeCheckoutSession
                {
                    SessionId = $"cs_test_{SessionsCreated}",
                    Url = $"https://checkout.stripe.example/cs_test_{SessionsCreated}",
                    Status = "open",
                    PaymentStatus = "unpaid",
                    ExpiresAt = request.ExpiresAt,
                    ClientReferenceId = request.IntentId,
                };
                _byKey[request.IdempotencyKey] = (fingerprint, session);
                LastUrl = session.Url;
                if (LoseNextResponse)
                {
                    LoseNextResponse = false;
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
                var found = _byKey.Values.Select(entry => entry.Session).FirstOrDefault(session => session.SessionId == sessionId);
                return Task.FromResult(found is null
                    ? AZOAResult<StripeCheckoutSession>.Failure("No such session.")
                    : AZOAResult<StripeCheckoutSession>.Success(found));
            }
        }

        public Task<AZOAResult<StripeCheckoutSession>> ExpireCheckoutSessionAsync(
            StripeCredentials credentials, string sessionId, string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<StripeCheckoutSession>.Failure("The creation-fence tests never expire a session."));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
