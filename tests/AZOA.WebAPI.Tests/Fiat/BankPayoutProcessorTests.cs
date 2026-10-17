using System.Text.Json;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class BankPayoutProcessorTests
{
    [Fact]
    public async Task A_test_mode_bank_payout_cannot_complete_a_live_cash_out()
    {
        var fixture = new Fixture { Status = "paid" };
        fixture.Options.Environment = RailEnvironmentNames.Live;
        var result = await fixture.SendAsync();
        result.IsError.Should().BeTrue();
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.BankPending);
        fixture.Row.PaidAt.Should().BeNull();
    }

    [Fact]
    public async Task A_funding_transfer_does_not_mark_cash_out_paid_until_the_bank_payout_is_paid()
    {
        var fixture = new Fixture();
        await fixture.SendAsync();
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.BankPending);
        fixture.Row.ProviderPayoutId.Should().Be("po_bank");
        fixture.Row.PaidAt.Should().BeNull();

        fixture.Status = "paid";
        await fixture.SendAsync();
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.Paid);
        fixture.Row.PaidAt.Should().NotBeNull();
        fixture.CreateKeys.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_creation_outcome_reuses_the_same_key_and_never_marks_paid()
    {
        var fixture = new Fixture { ErrorCode = FiatPayoutErrors.PayoutBusyCode };
        await fixture.SendAsync();
        await fixture.SendAsync();
        fixture.CreateKeys.Should().HaveCount(2);
        fixture.CreateKeys.Distinct().Should().ContainSingle();
        fixture.Row.BankPayoutAttempt.Should().Be(0);
        fixture.Row.BankPayoutStartedAt.Should().NotBeNull();
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.BankPending);
    }

    [Fact]
    public async Task Definitive_insufficient_funds_can_retry_when_funds_become_available()
    {
        var fixture = new Fixture { ErrorCode = FiatPayoutErrors.AwaitingFundsCode };
        await fixture.SendAsync();
        fixture.Row.BankPayoutAttempt.Should().Be(1);
        fixture.Row.BankPayoutStartedAt.Should().BeNull();
        fixture.ErrorCode = null;
        await fixture.SendAsync();
        fixture.CreateKeys.Distinct().Should().HaveCount(2);
        fixture.Row.ProviderPayoutId.Should().Be("po_bank");
    }

    [Fact]
    public async Task Unknown_outcome_beyond_provider_key_retention_requires_reconciliation_without_resending()
    {
        var fixture = new Fixture();
        fixture.Row.BankPayoutStartedAt = DateTimeOffset.UtcNow.AddDays(-2);
        await fixture.SendAsync();
        fixture.CreateKeys.Should().BeEmpty();
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task Bank_failure_requires_fund_reconciliation_without_claiming_units_were_returned(string status)
    {
        var fixture = new Fixture { Status = status };
        await fixture.SendAsync();
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
        fixture.Row.ProviderPayoutId.Should().Be("po_bank");
        fixture.Row.UnitsReturnedTransactionId.Should().BeNull();
        fixture.Row.PaidAt.Should().BeNull();
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task Delayed_bank_failure_preserves_funding_reversal(string status)
    {
        var fixture = new Fixture { Status = status };
        fixture.Row.Status = FiatPayoutStatuses.Reversed;
        fixture.Row.ProviderPayoutId = "po_bank";
        fixture.Row.BankPayoutStartedAt = DateTimeOffset.UtcNow;
        await fixture.Processor.ReconcileAsync(fixture.Copy(), "acct_owner", "po_bank", default);
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.Reversed);
        fixture.Row.UnitsReturnedTransactionId.Should().BeNull();
    }

    [Fact]
    public async Task A_bank_event_must_match_the_persisted_connected_account()
    {
        var fixture = new Fixture();
        fixture.Row.BankPayoutStartedAt = DateTimeOffset.UtcNow;
        (await fixture.Processor.ReconcileAsync(fixture.Copy(), "acct_other", "po_bank", default))
            .Should().Be(FiatEventOutcome.Ignored);
        fixture.Gateway.Verify(x => x.GetBankPayoutAsync(It.IsAny<StripeCredentials>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Paid_event_reconciles_a_creation_response_lost_before_the_payout_reference_was_saved()
    {
        var fixture = new Fixture { Status = "paid" };
        fixture.Row.BankPayoutStartedAt = DateTimeOffset.UtcNow;
        (await fixture.Processor.ReconcileAsync(fixture.Copy(), "acct_owner", "po_bank", default))
            .Should().Be(FiatEventOutcome.Applied);
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.Paid);
        fixture.Row.ProviderPayoutId.Should().Be("po_bank");
        fixture.CreateKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Automatic_schedule_blocks_manual_payout_creation()
    {
        var fixture = new Fixture { Schedule = "daily" };
        await fixture.SendAsync();
        fixture.CreateKeys.Should().BeEmpty();
        fixture.Row.Status.Should().Be(FiatPayoutStatuses.NeedsOperator);
    }

    private sealed class Fixture
    {
        public FiatPayout Row { get; private set; } = new()
        {
            Environment = "test", ChainNetwork = "Devnet", Id = Guid.NewGuid().ToString("N"), ConnectedAccountId = "acct_owner",
            ProviderTransferId = "tr_funded", AmountMinorUnits = 500, CurrencyCode = "usd",
            QuantityBaseUnits = "50000", Status = FiatPayoutStatuses.BankPending,
        };
        public string Status { get; set; } = "pending";
        public string Schedule { get; init; } = "manual";
        public string? ErrorCode { get; set; }
        public List<string> CreateKeys { get; } = [];
        public Mock<IStripeConnectGateway> Gateway { get; } = new(MockBehavior.Strict);
        public BankPayoutProcessor Processor { get; }
        public FiatOptions Options { get; } = new();

        public Fixture()
        {
            var ledger = new Mock<IFiatPayoutLedger>(MockBehavior.Strict);
            ledger.Setup(x => x.UpdateIfVersionAsync(It.IsAny<FiatPayout>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FiatPayout candidate, long expected, CancellationToken _) =>
                {
                    if (Row.Version != expected) return AZOAResult<FiatPayout>.Failure("Version conflict.");
                    Row = Clone(candidate);
                    return AZOAResult<FiatPayout>.Success(Copy());
                });
            Gateway.Setup(x => x.GetTransferAsync(It.IsAny<StripeCredentials>(), "tr_funded", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeTransfer>.Success(new() { TransferId = "tr_funded", IntentId = Row.Id,
                    Destination = "acct_owner", AmountMinorUnits = 500, Currency = "usd" }));
            Gateway.Setup(x => x.GetConnectedAccountAsync(It.IsAny<StripeCredentials>(), "acct_owner", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeConnectedAccount>.Success(new StripeConnectedAccount
                { AccountId = "acct_owner", PayoutSchedule = Schedule, PayoutsEnabled = true }));
            Gateway.Setup(x => x.CreateBankPayoutAsync(It.IsAny<StripeCredentials>(), It.IsAny<StripeTransferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((StripeCredentials _, StripeTransferRequest request, CancellationToken __) =>
                {
                    CreateKeys.Add(request.IdempotencyKey);
                    request.ConnectedAccountId.Should().Be("acct_owner");
                    return ErrorCode is { } code ? AZOAResult<StripeBankPayout>.FailureWithCode("Provider unavailable.", code)
                        : AZOAResult<StripeBankPayout>.Success(BankPayout());
                });
            Gateway.Setup(x => x.GetBankPayoutAsync(It.IsAny<StripeCredentials>(), "acct_owner", "po_bank", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeBankPayout>.Success(BankPayout()));
            Processor = new(Options, ledger.Object, Gateway.Object, NullLogger.Instance, TimeProvider.System);
        }

        public Task<AZOAResult<OffRampStepOutcome>> SendAsync() => Processor.SendAsync(Copy(), default);
        public FiatPayout Copy() => Clone(Row);
        private static FiatPayout Clone(FiatPayout row) => JsonSerializer.Deserialize<FiatPayout>(JsonSerializer.Serialize(row))!;
        private StripeBankPayout BankPayout() => new()
        {
            PayoutId = "po_bank", AmountMinorUnits = 500, Currency = "usd", Status = Status, IntentId = Row.Id,
        };
    }
}
