using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class OnRampRecoveryTests
{
    [Fact]
    public async Task ProvenPreEffectAllocationFailureTransfersAtomicallyToRefund()
    {
        var fixture = new Fixture();
        var key = OnRampRecovery.IssuanceLedgerKey(fixture.Intent.ToString("N"));
        await fixture.Idempotency.TryClaimAsync(key, "fiat_allocation", default);
        await fixture.Idempotency.RecordNoEffectFailureAsync(key, "Identity gate refused before value effects", default);
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Refund after identity refusal", default)).Result!.Status.Should().Be(OnRampLotStatuses.Refunded);
        fixture.CreateCount.Should().Be(1);
        (await fixture.Idempotency.TryClaimAsync(key, "fiat_allocation", default)).Won.Should().BeFalse();
    }
    [Theory]
    [InlineData(true, true)]   // refused before any value moved (e.g. KYC): resume frees the key
    [InlineData(false, false)] // outcome unknown: the claim keeps blocking re-issuance
    public async Task ResumeReleasesOnlyAProvenNoEffectAllocationFailure(bool provenNoEffect, bool reissuable)
    {
        var fixture = new Fixture();
        var key = OnRampRecovery.IssuanceLedgerKey(fixture.Intent.ToString("N"));
        await fixture.Idempotency.TryClaimAsync(key, "fiat_allocation", default);
        if (provenNoEffect) await fixture.Idempotency.RecordNoEffectFailureAsync(key, "KYC_FORBIDDEN: refused", default);
        else await fixture.Idempotency.FailAsync(key, "Outcome cannot be established", default);

        await fixture.Service.ResumeAsync(fixture.Intent, fixture.Actor, "Identity scope fixed", default);

        (await fixture.Idempotency.TryClaimAsync(key, "fiat_allocation", default)).Won.Should().Be(reissuable);
    }

    [Fact]
    public async Task RefusedUnissuedPaymentRefundsOnceAndExcludesFutureAllocation()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Customer requested refund", default);
        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(OnRampLotStatuses.Refunded);
        result.Result.RecoveryAuditJson.Should().Contain(fixture.Actor.ToString());
        fixture.CreateCount.Should().Be(1);
        var lateAllocation = await fixture.Idempotency.TryClaimAsync(OnRampRecovery.IssuanceLedgerKey(fixture.Intent.ToString("N")), "allocate", default);
        lateAllocation.Won.Should().BeFalse();
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Same request", default)).IsError.Should().BeFalse();
        fixture.CreateCount.Should().Be(1);
        (await fixture.Service.ResumeAsync(fixture.Intent, fixture.Actor, "Cannot issue after refund", default)).IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingAllocationEvenWithoutOperationIdBlocksRefund(bool terminalFailure)
    {
        var fixture = new Fixture();
        var key = OnRampRecovery.IssuanceLedgerKey(fixture.Intent.ToString("N"));
        await fixture.Idempotency.TryClaimAsync(key, "allocate", default);
        if (terminalFailure) await fixture.Idempotency.FailAsync(key, "Outcome cannot be established", default);
        fixture.Current.OperationId.Should().BeNull();
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Must reconcile issuance", default)).IsError.Should().BeTrue();
        fixture.CreateCount.Should().Be(0);
        fixture.Current.Status.Should().Be(OnRampLotStatuses.Refused);
    }

    [Fact]
    public async Task PaymentMismatchNeverCallsRefundProvider()
    {
        var fixture = new Fixture { ProviderAmount = 999 };
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Refund review", default)).IsError.Should().BeTrue();
        fixture.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task PendingRefundIsRefetchedAndFinalizedWithoutAnotherCreate()
    {
        var fixture = new Fixture { RefundStatus = "pending" };
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Refund review", default)).Result!.Status.Should().Be(OnRampLotStatuses.RefundPending);
        fixture.RefundStatus = "succeeded";
        (await fixture.Service.ReconcileAsync(fixture.Snapshot, default)).Result!.Status.Should().Be(OnRampLotStatuses.Refunded);
        fixture.CreateCount.Should().Be(1);
        fixture.GetCount.Should().Be(1);
    }

    [Fact]
    public async Task UnknownRefundPastRetryWindowRequiresAuthoritativeReference()
    {
        var fixture = new Fixture { LoseRefundResponse = true };
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Refund review", default)).IsError.Should().BeTrue();
        fixture.Current.RefundStartedAt = DateTimeOffset.UtcNow.AddHours(-24);
        (await fixture.Service.ReconcileAsync(fixture.Snapshot, default)).IsError.Should().BeTrue();
        fixture.CreateCount.Should().Be(1);
        fixture.LoseRefundResponse = false;
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Provider reference reconciled", default, "re_test")).Result!.Status.Should().Be(OnRampLotStatuses.Refunded);
        fixture.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task UnknownRefundPastRetryWindowIsSettledFromThePaymentsRefundList()
    {
        // Stripe made the refund and only the response was lost; the payment's own refund list names it.
        var fixture = new Fixture { LoseRefundResponse = true };
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Refund review", default)).IsError.Should().BeTrue();
        fixture.Current.RefundStartedAt = DateTimeOffset.UtcNow.AddHours(-24);
        fixture.ListedRefunds = new([new("re_test", "pi_test", 1000, "usd", "succeeded", fixture.Intent.ToString("N"))], HasMore: false);

        (await fixture.Service.ReconcileAsync(fixture.Snapshot, default)).Result!.Status.Should().Be(OnRampLotStatuses.Refunded);
        fixture.CreateCount.Should().Be(1, "the refund that exists is adopted, never created again");
    }

    [Fact]
    public async Task ProvablyAbsentRefundPastRetryWindowOpensExactlyOneNewAttempt()
    {
        var fixture = new Fixture { LoseRefundResponse = true };
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Refund review", default)).IsError.Should().BeTrue();
        fixture.Current.RefundStartedAt = DateTimeOffset.UtcNow.AddHours(-24);
        fixture.ListedRefunds = new([], HasMore: false);
        fixture.LoseRefundResponse = false;

        (await fixture.Service.ReconcileAsync(fixture.Snapshot, default)).Result!.Status.Should().Be(OnRampLotStatuses.Refunded);
        fixture.CreateCount.Should().Be(2);
        fixture.Current.RefundAttempt.Should().Be(1, "the expired key is never replayed; a new attempt key is used");
    }

    [Fact]
    public async Task TerminalFailedRefundRetriesOnceForExpectedAttempt()
    {
        var fixture = new Fixture { RefundStatus = "failed" };
        await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Initial refund", default);
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Retry proven failure", default,
            retryFailed: true, expectedRefundAttempt: 0)).IsError.Should().BeFalse();
        fixture.Current.RefundAttempt.Should().Be(1);
        fixture.CreateCount.Should().Be(2);
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Duplicate operator request", default,
            retryFailed: true, expectedRefundAttempt: 0)).IsError.Should().BeTrue();
        fixture.CreateCount.Should().Be(2);
    }

    [Fact]
    public async Task CrossModeLotCannotIssueRefundRequest()
    {
        var fixture = new Fixture();
        fixture.Current.Environment = "live";
        (await fixture.Service.RefundAsync(fixture.Intent, fixture.Actor, "Wrong mode", default)).IsError.Should().BeTrue();
        fixture.CreateCount.Should().Be(0);
        fixture.GetCount.Should().Be(0);
    }

    private sealed class Fixture
    {
        public Guid Intent { get; } = Guid.NewGuid();
        public Guid Actor { get; } = Guid.NewGuid();
        public FakeIdempotencyStore Idempotency { get; } = new();
        public OnRampLot Current { get; set; }
        public OnRampLot Snapshot => Clone(Current);
        public long ProviderAmount { get; set; } = 1000;
        public string RefundStatus { get; set; } = "succeeded";
        public bool LoseRefundResponse { get; set; }
        /// <summary>The payment's refund list as Stripe would return it; null = the read fails.</summary>
        public StripeRefundList? ListedRefunds { get; set; }
        public int CreateCount { get; private set; }
        public int GetCount { get; private set; }
        public OnRampRecovery Service { get; }

        public Fixture()
        {
            Current = new OnRampLot { Environment = "test", ChainNetwork = "Devnet", Id = OnRampLot.RecordIdFor(Intent.ToString("N")), IntentId = Intent.ToString("N"),
                SubjectUserId = Guid.NewGuid().ToString("N"), Status = OnRampLotStatuses.Refused,
                FiatMinorUnits = 1000, CurrencyCode = "USD", SourceCheckoutSessionId = "cs_test", SourcePaymentIntentId = "pi_test" };
            var lots = new Mock<IOnRampLotLedger>();
            lots.Setup(x => x.GetByIntentAsync(Intent.ToString("N"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AZOAResult<OnRampLot?> { Result = Clone(Current) });
            lots.Setup(x => x.UpdateIfVersionAsync(It.IsAny<OnRampLot>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((OnRampLot row, long version, CancellationToken _) =>
                {
                    if (version != Current.Version) return AZOAResult<OnRampLot>.Failure("Conflict");
                    Current = Clone(row);
                    return AZOAResult<OnRampLot>.Success(Clone(Current));
                });
            var options = new FiatOptions { Enabled = true };
            var gateway = new Mock<IStripeGateway>();
            gateway.Setup(x => x.GetCheckoutSessionAsync(It.IsAny<StripeCredentials>(), "cs_test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeCheckoutSession>.Success(new()
                { SessionId = "cs_test", PaymentIntentId = "pi_test", PaymentStatus = "paid", Status = "complete",
                    AmountTotalMinorUnits = ProviderAmount, Currency = "usd", ClientReferenceId = Intent.ToString("N") }));
            var refunds = new Mock<IStripeRefundGateway>();
            StripeRefund Evidence() => new("re_test", "pi_test", 1000, "usd", RefundStatus, Intent.ToString("N"));
            refunds.Setup(x => x.CreateRefundAsync(It.IsAny<StripeCredentials>(), "pi_test", 1000, Intent.ToString("N"),
                    It.Is<string>(key => key.StartsWith($"azoa-on-ramp-refund-{Intent:N}-")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { CreateCount++; return LoseRefundResponse ? AZOAResult<StripeRefund>.Failure("Response lost") : AZOAResult<StripeRefund>.Success(Evidence()); });
            refunds.Setup(x => x.GetRefundAsync(It.IsAny<StripeCredentials>(), "re_test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { GetCount++; return AZOAResult<StripeRefund>.Success(Evidence()); });
            refunds.Setup(x => x.ListRefundsForPaymentAsync(It.IsAny<StripeCredentials>(), "pi_test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ListedRefunds is { } page
                    ? AZOAResult<StripeRefundList>.Success(page)
                    : AZOAResult<StripeRefundList>.Failure("Refund list unavailable"));
            // The resumed settlement stops at the intent read; these tests own the ledger keys, not issuance.
            var sessions = new Mock<IHostedSessionStore>();
            sessions.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<HostedSession>.Failure("Intent unavailable"));
            var handler = new OnRampSettlementHandler(options, new(), gateway.Object, sessions.Object,
                Mock.Of<ITenantPriceScheduleStore>(), lots.Object, Mock.Of<IPlatformTokenGenesis>(), Mock.Of<IAllocationManager>(),
                Mock.Of<IRealValueKycGate>(), Mock.Of<IValueRailProviderFactory>(), NullLogger<OnRampSettlementHandler>.Instance);
            Service = new(lots.Object, Idempotency, gateway.Object, refunds.Object, options, handler, TimeProvider.System);
        }

        private static OnRampLot Clone(OnRampLot row) => JsonSerializer.Deserialize<OnRampLot>(JsonSerializer.Serialize(row))!;
    }
}
