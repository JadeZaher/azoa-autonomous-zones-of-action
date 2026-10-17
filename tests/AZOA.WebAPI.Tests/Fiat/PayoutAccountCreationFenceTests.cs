// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>D1: one Stripe connected account per payout-account claim, across races, lost responses and replays.</summary>
public sealed class PayoutAccountCreationFenceTests
{
    private const string ReturnOrigin = "https://app.example.test";

    [Fact]
    public async Task Concurrent_setups_while_the_first_create_is_in_flight_open_exactly_one_account()
    {
        var harness = new Harness();
        harness.Stripe.HoldFirstCreate();
        var subject = Guid.NewGuid();

        var first = harness.Service.BeginSetupAsync(subject, tenantId: null);
        await harness.Stripe.FirstCreateEntered;
        var racers = Enumerable.Range(0, 5).Select(_ => harness.Service.BeginSetupAsync(subject, tenantId: null)).ToArray();
        await Task.WhenAll(racers);
        harness.Stripe.ReleaseFirstCreate();
        var winner = await first;
        var after = await harness.Service.BeginSetupAsync(subject, tenantId: null);

        winner.IsError.Should().BeFalse(winner.Message);
        after.IsError.Should().BeFalse(after.Message);
        harness.Stripe.AccountsOpened.Should().ContainSingle();
        var account = harness.Stripe.AccountsOpened.Single();
        (await harness.AttachedAccountAsync(subject)).Should().Be(account);
        harness.Stripe.LinkedAccounts.Should().OnlyContain(linked => linked == account);
    }

    [Fact]
    public async Task Concurrent_cold_setups_open_exactly_one_account_and_every_success_uses_it()
    {
        var harness = new Harness();
        var subject = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => harness.Service.BeginSetupAsync(subject, tenantId: null))));

        results.Should().Contain(result => !result.IsError);
        harness.Stripe.AccountsOpened.Should().ContainSingle();
        (await harness.AttachedAccountAsync(subject)).Should().Be(harness.Stripe.AccountsOpened.Single());
        harness.Stripe.LinkedAccounts.Should().OnlyContain(linked => linked == harness.Stripe.AccountsOpened.Single());
    }

    [Fact]
    public async Task A_lost_create_response_is_replayed_onto_the_same_account()
    {
        var harness = new Harness();
        var subject = Guid.NewGuid();
        harness.Stripe.LoseNextResponse = true;

        var lost = await harness.Service.BeginSetupAsync(subject, tenantId: null);
        var replayed = await harness.Service.BeginSetupAsync(subject, tenantId: null);

        lost.IsError.Should().BeTrue();
        replayed.IsError.Should().BeFalse(replayed.Message);
        harness.Stripe.AccountsOpened.Should().ContainSingle();
        harness.Stripe.CreateKeys.Distinct().Should().ContainSingle("a replay must reuse the claim-bound key");
        (await harness.AttachedAccountAsync(subject)).Should().Be(harness.Stripe.AccountsOpened.Single());
    }

    [Fact]
    public async Task A_lost_create_past_the_idempotency_window_needs_an_operator_instead_of_a_second_account()
    {
        var harness = new Harness();
        var subject = Guid.NewGuid();
        harness.Stripe.LoseNextResponse = true;
        await harness.Service.BeginSetupAsync(subject, tenantId: null);

        harness.Clock.Advance(TimeSpan.FromHours(24));
        var late = await harness.Service.BeginSetupAsync(subject, tenantId: null);

        late.IsError.Should().BeTrue();
        late.Code.Should().Be(AzoaErrorCodes.Conflict);
        harness.Stripe.AccountsOpened.Should().ContainSingle();
        (await harness.AttachedAccountAsync(subject)).Should().BeNull();
    }

    [Fact]
    public async Task Reading_status_first_claims_nothing_and_setup_still_opens_the_account()
    {
        var harness = new Harness();
        var subject = Guid.NewGuid();

        var status = await harness.Service.GetStatusAsync(subject);
        var claimAfterRead = await harness.Store.GetByAdmissionAsync(
            PayoutAccountFlow.FlowKind, FiatRailIds.StripeProviderKey, PayoutAccountFlow.AdmissionKeyFor(subject));
        var setup = await harness.Service.BeginSetupAsync(subject, tenantId: null);

        status.Result!.AccountOpened.Should().BeFalse();
        claimAfterRead.Result.Should().BeNull();
        setup.IsError.Should().BeFalse(setup.Message);
        harness.Stripe.AccountsOpened.Should().ContainSingle();
    }

    private sealed class Harness
    {
        public InMemoryHostedSessionStore Store { get; } = new();
        public FakeStripeConnect Stripe { get; } = new();
        public MutableClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        public PayoutAccountService Service { get; }

        public Harness()
        {
            var fiat = new FiatOptions { Enabled = true, Environment = RailEnvironmentNames.Test, ApiBaseUrl = "https://api.example" };
            fiat.Test.IsEnabled = true;
            fiat.Test.SecretKey = "sk_test_fence_fixture";
            fiat.Test.WebhookSecret = "whsec_fence_fixture";
            fiat.Checkout.AllowedReturnOrigins.Add(ReturnOrigin);
            var offRamp = new OffRampOptions
            {
                Enabled = true,
                OnboardingReturnUrl = ReturnOrigin + "/payout/return",
                OnboardingRefreshUrl = ReturnOrigin + "/payout/refresh",
            };
            Service = new PayoutAccountService(fiat, offRamp, Stripe, Store, NullLogger<PayoutAccountService>.Instance, Clock);
        }

        public async Task<string?> AttachedAccountAsync(Guid subject)
            => (await Store.GetByAdmissionAsync(PayoutAccountFlow.FlowKind, FiatRailIds.StripeProviderKey,
                PayoutAccountFlow.AdmissionKeyFor(subject))).Result?.ProviderSessionId;
    }

    /// <summary>Models Stripe idempotency: a key replays its account, a concurrent same-key request is refused, a new key opens a new account.</summary>
    private sealed class FakeStripeConnect : IStripeConnectGateway
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _accountsByKey = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
        private readonly List<string> _opened = [];
        private readonly List<string> _keys = [];
        private readonly List<string> _linked = [];
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _holdFirst;

        public bool LoseNextResponse { get; set; }
        public Task FirstCreateEntered => _firstEntered.Task;
        public IReadOnlyList<string> AccountsOpened { get { lock (_gate) return _opened.ToArray(); } }
        public IReadOnlyList<string> CreateKeys { get { lock (_gate) return _keys.ToArray(); } }
        public IReadOnlyList<string> LinkedAccounts { get { lock (_gate) return _linked.ToArray(); } }

        public void HoldFirstCreate() => _holdFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseFirstCreate() => _holdFirst?.TrySetResult();

        public async Task<AZOAResult<StripeConnectedAccount>> CreateConnectedAccountAsync(
            StripeCredentials credentials, StripeConnectedAccountRequest request, CancellationToken ct = default)
        {
            Task? hold = null;
            lock (_gate)
            {
                _keys.Add(request.IdempotencyKey);
                if (!_inFlight.Add(request.IdempotencyKey))
                    return FiatPayoutErrors.RailBusy<StripeConnectedAccount>();
                if (_firstEntered.TrySetResult()) hold = _holdFirst?.Task;
            }

            if (hold is not null) await hold;
            await Task.Yield();

            lock (_gate)
            {
                _inFlight.Remove(request.IdempotencyKey);
                if (!_accountsByKey.TryGetValue(request.IdempotencyKey, out var account))
                {
                    account = "acct_" + Guid.NewGuid().ToString("N")[..16];
                    _accountsByKey[request.IdempotencyKey] = account;
                    _opened.Add(account);
                }

                if (LoseNextResponse)
                {
                    LoseNextResponse = false;
                    return FiatPayoutErrors.RailBusy<StripeConnectedAccount>();
                }

                return AZOAResult<StripeConnectedAccount>.Success(new StripeConnectedAccount { AccountId = account });
            }
        }

        public Task<AZOAResult<StripeConnectedAccount>> GetConnectedAccountAsync(
            StripeCredentials credentials, string accountId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<StripeConnectedAccount>.Success(new StripeConnectedAccount { AccountId = accountId }));

        public Task<AZOAResult<StripeAccountLink>> CreateAccountLinkAsync(
            StripeCredentials credentials, StripeAccountLinkRequest request, CancellationToken ct = default)
        {
            lock (_gate) _linked.Add(request.AccountId);
            return Task.FromResult(AZOAResult<StripeAccountLink>.Success(
                new StripeAccountLink { Url = "https://connect.stripe.test/setup/" + request.AccountId }));
        }

        public Task<AZOAResult<StripeTransferReversal>> ReverseTransferAsync(StripeCredentials credentials,
            string transferId, long amount, string payoutId, string idempotencyKey, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<StripeTransfer>> CreatePayoutTransferAsync(
            StripeCredentials credentials, StripeTransferRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<StripeTransfer>> GetTransferAsync(
            StripeCredentials credentials, string transferId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<StripeTransferList>> ListTransfersByGroupAsync(
            StripeCredentials credentials, string transferGroup, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<StripeConnectedAccount>> ConfigureManualPayoutsAsync(
            StripeCredentials credentials, string accountId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<StripeBankPayout>> CreateBankPayoutAsync(
            StripeCredentials credentials, StripeTransferRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<StripeBankPayout>> GetBankPayoutAsync(
            StripeCredentials credentials, string accountId, string payoutId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
