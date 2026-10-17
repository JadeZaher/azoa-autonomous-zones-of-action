using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class FailedBankPayoutRecoveryTests
{
    [Fact]
    public async Task Partial_then_full_recovery_restores_only_cumulative_difference()
    {
        var f = new Fixture { Reversed = 40 };
        (await f.Run()).IsError.Should().BeFalse();
        f.Restored.Should().Be(400);
        f.Reversed = 100;
        (await f.Run()).IsError.Should().BeFalse();
        (await f.Run()).IsError.Should().BeFalse();
        f.Restored.Should().Be(1000);
        f.Row.Status.Should().Be(FiatPayoutStatuses.Returned);
        f.Keys.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("pending")]
    public async Task Nonfailed_bank_payout_never_restores_units(string state)
    {
        var f = new Fixture { BankStatus = state, Reversed = 100 };
        (await f.Run()).IsError.Should().BeTrue();
        f.Restored.Should().Be(0);
    }

    [Fact]
    public async Task Changed_environment_makes_no_provider_call()
    {
        var f = new Fixture();
        f.Row.Environment = "live";
        (await f.Run()).IsError.Should().BeTrue();
        f.Gateway.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task BankBecomesPaidBetweenReads_HoldsReservedRestitution()
    {
        var f = new Fixture { Reversed = 100, ChangeBankOnSecondRead = true };
        (await f.Run()).IsError.Should().BeTrue();
        f.Restored.Should().Be(0);
        f.Row.RestitutionKey.Should().NotBeNull();
    }

    private sealed class Fixture
    {
        public FiatPayout Row = new() { Id = "cashout", Environment = "test", ChainNetwork = "Devnet",
            SubjectUserId = Guid.NewGuid().ToString("N"), AmountMinorUnits = 100, CurrencyCode = "usd",
            QuantityBaseUnits = "1000", ProviderPayoutId = "po_bank", ProviderTransferId = "tr_funds",
            ConnectedAccountId = "acct_owner", Status = FiatPayoutStatuses.NeedsOperator };
        public long Reversed;
        public string BankStatus = "failed";
        public bool ChangeBankOnSecondRead;
        private int bankReads;
        public BigInteger Restored;
        public HashSet<string> Keys = [];
        public Mock<IStripeConnectGateway> Gateway = new(MockBehavior.Strict);
        private readonly FailedBankPayoutRecovery service;
        public Fixture()
        {
            var ledger = new Mock<IFiatPayoutLedger>();
            ledger.Setup(x => x.UpdateIfVersionAsync(It.IsAny<FiatPayout>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FiatPayout candidate, long version, CancellationToken _) => {
                    if (Row.Version != version) return AZOAResult<FiatPayout>.Failure("Conflict");
                    Row = Copy(candidate); return AZOAResult<FiatPayout>.Success(Copy(Row)); });
            Gateway.Setup(x => x.GetBankPayoutAsync(It.IsAny<StripeCredentials>(), "acct_owner", "po_bank", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeBankPayout>.Success(new() { PayoutId = "po_bank", IntentId = Row.Id,
                    AmountMinorUnits = 100, Currency = "usd", Status = ChangeBankOnSecondRead && ++bankReads > 1 ? "paid" : BankStatus }));
            Gateway.Setup(x => x.GetTransferAsync(It.IsAny<StripeCredentials>(), "tr_funds", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<StripeTransfer>.Success(new() { TransferId = "tr_funds", IntentId = Row.Id,
                    Destination = "acct_owner", AmountMinorUnits = 100, AmountReversedMinorUnits = Reversed, Currency = "usd" }));
            Gateway.Setup(x => x.ReverseTransferAsync(It.IsAny<StripeCredentials>(), "tr_funds", It.IsAny<long>(), Row.Id,
                It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<StripeTransferReversal>.Failure("Unknown outcome"));
            service = new(new FiatOptions(), ledger.Object, Gateway.Object, TimeProvider.System, (_, quantity, key, _) => {
                if (Keys.Add(key)) Restored += quantity;
                return Task.FromResult<string?>(null); });
        }
        public Task<AZOAResult<FiatPayout>> Run() => service.RecoverAsync(Copy(Row), Guid.NewGuid(), "Recover failed bank payout", default);
        private static FiatPayout Copy(FiatPayout row) => JsonSerializer.Deserialize<FiatPayout>(JsonSerializer.Serialize(row))!;
    }
}
