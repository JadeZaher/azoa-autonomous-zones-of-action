using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class PayoutReversalReconciliationTests
{
    [Fact]
    public async Task Increasing_partial_reversals_and_duplicates_reconcile_one_cumulative_debt()
    {
        var fixture = new Fixture();
        foreach (var amount in new long[] { 100, 250, 250, 100, 500, 250 })
        {
            fixture.AmountReversed = amount;
            (await fixture.DeliverAsync()).Should().Be(FiatEventOutcome.Applied);
        }

        fixture.Payout.ReversedMinorUnits.Should().Be(500);
        fixture.Payout.UnitsOwedBaseUnits.Should().Be("50000");
        fixture.Reversal!.AmountMinorUnits.Should().Be(500);
        fixture.Reversal.ReinstatementOwedBaseUnits.Should().Be("50000");
        fixture.Reversal.IsPartial.Should().BeFalse();
        fixture.Reversal.AccountFlagged.Should().BeFalse();
        fixture.Reversal.TargetQuantityBaseUnits.Should().Be("0");
        fixture.Reversal.Status.Should().Be(FiatReversalStatuses.NeedsOperator);
        fixture.Payout.Version.Should().Be(3);
        fixture.Reversal.Version.Should().Be(2);
    }

    [Fact]
    public async Task Ledger_commit_followed_by_failed_payout_save_recovers_even_from_stale_provider_read()
    {
        var fixture = new Fixture { AmountReversed = 100 };
        (await fixture.DeliverAsync()).Should().Be(FiatEventOutcome.Applied);
        fixture.AmountReversed = 300;
        fixture.FailPayoutSave = true;
        (await fixture.DeliverAsync()).Should().Be(FiatEventOutcome.Failed);
        fixture.Reversal!.AmountMinorUnits.Should().Be(300);
        fixture.Payout.ReversedMinorUnits.Should().Be(100);

        fixture.FailPayoutSave = false;
        fixture.AmountReversed = 100;
        (await fixture.DeliverAsync()).Should().Be(FiatEventOutcome.Applied);
        fixture.Payout.ReversedMinorUnits.Should().Be(300);
        fixture.Payout.UnitsOwedBaseUnits.Should().Be("30000");
        fixture.Reversal.Version.Should().Be(1);
    }

    [Fact]
    public async Task Ledger_compare_and_swap_failure_is_retryable_and_does_not_advance_payout()
    {
        var fixture = new Fixture { AmountReversed = 100 };
        await fixture.DeliverAsync();
        fixture.AmountReversed = 300;
        fixture.FailReversalSave = true;
        (await fixture.DeliverAsync()).Should().Be(FiatEventOutcome.Failed);
        fixture.Payout.ReversedMinorUnits.Should().Be(100);
        fixture.Reversal!.AmountMinorUnits.Should().Be(100);

        fixture.FailReversalSave = false;
        (await fixture.DeliverAsync()).Should().Be(FiatEventOutcome.Applied);
        fixture.Payout.ReversedMinorUnits.Should().Be(300);
        fixture.Reversal!.AmountMinorUnits.Should().Be(300);
    }

    [Theory]
    [InlineData("tr_other", "usd", 500, 100)]
    [InlineData("tr_payout", "eur", 500, 100)]
    [InlineData("tr_payout", "usd", 600, 100)]
    [InlineData("tr_payout", "usd", 500, 501)]
    public async Task Inconsistent_transfer_facts_do_not_create_debt(
        string transferId, string currency, long amount, long reversed)
    {
        var fixture = new Fixture
        {
            TransferId = transferId, Currency = currency,
            TransferAmount = amount, AmountReversed = reversed,
        };
        (await fixture.DeliverAsync()).Should().Be(FiatEventOutcome.Failed);
        fixture.Payout.Status.Should().Be(FiatPayoutStatuses.Paid);
        fixture.Reversal.Should().BeNull();
    }

    private sealed class Fixture
    {
        public FiatPayout Payout { get; private set; } = new()
        {
            Environment = "test", ChainNetwork = "Devnet", Id = Guid.NewGuid().ToString("N"), SubjectUserId = Guid.NewGuid().ToString("N"),
            ProviderTransferId = "tr_payout", AmountMinorUnits = 500,
            QuantityBaseUnits = "50000", CurrencyCode = "USD", Status = FiatPayoutStatuses.Paid,
        };
        public FiatReversal? Reversal { get; private set; }
        public long AmountReversed { get; set; }
        public long TransferAmount { get; init; } = 500;
        public string TransferId { get; init; } = "tr_payout";
        public string Currency { get; init; } = "usd";
        public bool FailPayoutSave { get; set; }
        public bool FailReversalSave { get; set; }

        public async Task<FiatEventOutcome> DeliverAsync()
        {
            var payouts = new Mock<IFiatPayoutLedger>(MockBehavior.Strict);
            payouts.Setup(x => x.GetAsync(Payout.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AZOAResult<FiatPayout?> { Result = Copy(Payout) });
            payouts.Setup(x => x.UpdateIfVersionAsync(
                    It.IsAny<FiatPayout>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FiatPayout row, long version, CancellationToken _) =>
                {
                    if (FailPayoutSave || Payout.Version != version)
                        return AZOAResult<FiatPayout>.Failure("Payout write conflict.");
                    Payout = Copy(row);
                    return AZOAResult<FiatPayout>.Success(Copy(Payout));
                });
            var reversals = new Mock<IFiatReversalLedger>(MockBehavior.Strict);
            reversals.Setup(x => x.ClaimAsync(It.IsAny<FiatReversal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FiatReversal row, CancellationToken _) =>
                {
                    var created = Reversal is null;
                    Reversal ??= Copy(row);
                    return AZOAResult<FiatReversalClaim>.Success(new(Copy(Reversal), created));
                });
            reversals.Setup(x => x.UpdateIfVersionAsync(
                    It.IsAny<FiatReversal>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FiatReversal row, long version, CancellationToken _) =>
                {
                    if (FailReversalSave || Reversal!.Version != version)
                        return AZOAResult<FiatReversal>.Failure("Reversal write conflict.");
                    Reversal = Copy(row);
                    return AZOAResult<FiatReversal>.Success(Copy(Reversal));
                });
            var connect = new Mock<IStripeConnectGateway>(MockBehavior.Strict);
            connect.Setup(x => x.GetTransferAsync(
                    It.IsAny<StripeCredentials>(), "tr_payout", It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<StripeTransfer>.Success(new StripeTransfer
                {
                    TransferId = TransferId, Currency = Currency, AmountMinorUnits = TransferAmount,
                    AmountReversedMinorUnits = AmountReversed, Reversed = AmountReversed == TransferAmount,
                }));
            var handler = new PayoutReversalEventHandler(
                new FiatOptions(), payouts.Object, reversals.Object, connect.Object,
                NullLogger<PayoutReversalEventHandler>.Instance);
            var context = new FiatEventContext(
                new FiatEvent { ProviderEventId = "evt_" + Guid.NewGuid().ToString("N") },
                PayoutReversalEventHandler.TransferReversedEventType, null, null, null,
                Guid.Parse(Payout.Id), Guid.Parse(Payout.SubjectUserId), RailEnvironment.Test);
            return await handler.ApplyAsync(context);
        }

        private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    }
}
