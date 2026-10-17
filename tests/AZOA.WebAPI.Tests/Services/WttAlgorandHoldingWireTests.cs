using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandHoldingWireTests
{
    [Fact]
    public void Custody_optin_funds_only_canonical_storage_and_requests_exact_asset()
    {
        var owner = new AlgoAccount(); var input = Parameters(owner);
        var transactions = WttAlgorandHoldingWire.Build(input);
        var payment = transactions[0].Should().BeOfType<PaymentTransaction>().Subject;
        payment.Amount.Should().Be(100_000);
        payment.Receiver.ToString().Should().Be(WttAlgorandStorage.Address(9, ulong.MaxValue, "native"u8));
        var call = transactions[1].Should().BeOfType<ApplicationNoopTransaction>().Subject;
        call.ApplicationId.Should().Be(9);
        call.Accounts.Should().Equal(payment.Receiver);
        call.ForeignAssets.Should().Equal(ulong.MaxValue);
        call.ApplicationArgs[0].Should().Equal("optin"u8.ToArray());
        call.ApplicationArgs[1].Should().Equal(Enumerable.Repeat((byte)255, 8));
        WttAlgorandHoldingWire.ValidateSigned(input, Sign(transactions, owner)).Should().HaveCount(2);
    }

    [Fact]
    public void Recipient_optin_requires_recipient_signature_and_no_transfer_or_close()
    {
        var owner = new AlgoAccount();
        var input = Parameters(owner) with { Stage = "RecipientOptIn", ApplicationFee = 0 };
        var transactions = WttAlgorandHoldingWire.Build(input);
        var transfer = transactions.Single().Should().BeOfType<AssetTransferTransaction>().Subject;
        transfer.AssetAmount.Should().Be(0);
        transfer.AssetReceiver.Should().Be(owner.Address);
        transfer.Sender.Should().Be(owner.Address);
        WttAlgorandHoldingWire.ValidateSigned(input, Sign(transactions, owner)).Should().ContainSingle();
        transfer.AssetReceiver = Address.ForApplication(9);
        Action altered = () => WttAlgorandHoldingWire.ValidateSigned(input, Sign(transactions, owner));
        altered.Should().Throw<InvalidDataException>();
        var wrongOwner = new AlgoAccount();
        Action impersonated = () => WttAlgorandHoldingWire.ValidateSigned(input, Sign(WttAlgorandHoldingWire.Build(input), wrongOwner));
        impersonated.Should().Throw<InvalidOperationException>();
    }

    private static WttAlgorandHoldingParameters Parameters(AlgoAccount owner) => new("CustodyOptIn", "bridge:holding:0",
        owner.Address.ToString(), 9, ulong.MaxValue, 1000, 1000, 2000, 100, 200,
        Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray()), "localnet-v1");
    private static string[] Sign(IReadOnlyList<Transaction> transactions, AlgoAccount owner)
        => transactions.Select(transaction => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(owner.SignTransaction(transaction)))).ToArray();
}
