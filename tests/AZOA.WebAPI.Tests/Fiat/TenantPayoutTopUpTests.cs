using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>D-8 top-ups: the float is credited net of Stripe fees, bound to the server-recorded tenant, once.</summary>
public sealed class TenantPayoutTopUpTests
{
    private static readonly Guid Tenant = new("44444444444444444444444444444444");
    private static readonly Guid IntentId = new("55555555555555555555555555555555");

    [Fact]
    public async Task A_settled_top_up_credits_the_net_once_and_never_matures_without_a_dispute_window()
    {
        var fixture = new Fixture();

        var first = await fixture.Handler.ApplyAsync(fixture.Context());
        var replay = await fixture.Handler.ApplyAsync(fixture.Context());

        first.Should().Be(FiatEventOutcome.Applied);
        replay.Should().Be(FiatEventOutcome.Applied);
        var credit = fixture.Ledger.Credits.Should().ContainSingle().Subject;
        credit.NetMinorUnits.Should().Be(970, "the tenant bears Stripe's fee");
        credit.State.Should().Be(TenantPayoutCreditStates.Pending);
        credit.AvailableAt.Should().BeNull("a rail with no declared reversal window never matures a credit");
        (await fixture.Funding.DescribeAsync(Tenant)).Single().AvailableMinorUnits.Should().Be(0);
    }

    [Fact]
    public async Task A_top_up_intent_whose_subject_is_not_the_tenant_in_its_key_credits_nothing()
    {
        var fixture = new Fixture { Subject = Guid.NewGuid() };

        var outcome = await fixture.Handler.ApplyAsync(fixture.Context());

        outcome.Should().Be(FiatEventOutcome.Failed);
        fixture.Ledger.Credits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refunded_top_up_reverses_its_pending_credit_by_the_gross()
    {
        var fixture = new Fixture();
        await fixture.Handler.ApplyAsync(fixture.Context());
        var credit = fixture.Ledger.Credits.Single();
        var reversal = new TenantPayoutTopUpReversalHandler(fixture.Fiat, fixture.Facts.Object, fixture.Ledger, fixture.Funding);
        fixture.Facts.Setup(f => f.GetChargeAsync(It.IsAny<StripeCredentials>(), "ch_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChargeReversalFacts>.Success(
                new ChargeReversalFacts("ch_1", "pi_1", 1000, 1000, "usd", true, false, false, null, null)));

        var outcome = await reversal.ApplyAsync(new FiatEventContext(new FiatEvent(), "charge.refunded", null, "pi_1", "ch_1",
            null, null, RailEnvironment.Test));

        outcome.Should().Be(FiatEventOutcome.Applied);
        fixture.Ledger.Credits.Single(c => c.Id == credit.Id).ReversedMinorUnits.Should().Be(1000);
        var view = (await fixture.Funding.DescribeAsync(Tenant)).Single();
        view.PendingCreditMinorUnits.Should().Be(-30, "a full refund returns the gross, so the net-of-fee credit goes negative");
        view.Consistent.Should().BeTrue();
    }

    private sealed class Fixture
    {
        public Guid Subject { get; init; } = Tenant;
        public FiatOptions Fiat { get; } = new() { Enabled = true, Test = { IsEnabled = true, SecretKey = "sk_test_x" } };
        public InMemoryTenantPayoutLedger Ledger { get; } = new(TimeProvider.System);
        public Mock<IReversalFactSource> Facts { get; } = new();
        public TenantPayoutFunding Funding { get; }
        public TenantPayoutTopUpHandler Handler => BuildHandler();

        public Fixture()
        {
            Funding = new TenantPayoutFunding(Ledger, new FakeIdempotencyStore(), Mock.Of<IStripeConnectGateway>(), Fiat,
                TimeProvider.System, NullLogger<TenantPayoutFunding>.Instance);
            Facts.Setup(f => f.GetPaymentBalanceAsync(It.IsAny<StripeCredentials>(), "pi_1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<PaymentBalanceFacts>.Success(new PaymentBalanceFacts("ch_1", 1000, 970, "usd", false)));
        }

        public FiatEventContext Context() => new(new FiatEvent(), "checkout.session.completed", "cs_1", "pi_1", null,
            IntentId, null, RailEnvironment.Test);

        private TenantPayoutTopUpHandler BuildHandler()
        {
            var sessions = new Mock<IHostedSessionStore>();
            sessions.Setup(s => s.GetByIdAsync(IntentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<HostedSession>.Success(new HostedSession
                {
                    Id = IntentId.ToString("N"), FlowKind = HostedSessionFlowKinds.FiatCheckout,
                    ProviderKey = FiatRailIds.StripeProviderKey, AdmissionKey = TenantPayoutTopUp.AdmissionKeyFor(Tenant, "t1"),
                    SubjectUserId = Subject.ToString("N"), ProviderSessionId = "cs_1",
                }));
            var gateway = new Mock<IStripeGateway>();
            gateway.Setup(g => g.GetCheckoutSessionAsync(It.IsAny<StripeCredentials>(), "cs_1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<StripeCheckoutSession>.Success(new StripeCheckoutSession
                {
                    SessionId = "cs_1", Status = "complete", PaymentStatus = "paid", PaymentIntentId = "pi_1",
                    AmountTotalMinorUnits = 1000, Currency = "usd", ClientReferenceId = IntentId.ToString("N"),
                }));
            return new TenantPayoutTopUpHandler(Fiat, sessions.Object, gateway.Object, Facts.Object,
                Mock.Of<IValueRailProviderFactory>(), Ledger, Funding, TimeProvider.System,
                NullLogger<TenantPayoutTopUpHandler>.Instance);
        }
    }
}
