using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandStorageWireTests
{
    [Fact]
    public void Exact_group_funds_storage_and_rekeys_only_to_bound_application()
    {
        var payer = new AlgoAccount();
        var input = Parameters(payer);
        var transactions = WttAlgorandStorageWire.Build(input);
        var payment = transactions[0].Should().BeOfType<PaymentTransaction>().Subject;
        var optIn = transactions[1].Should().BeOfType<ApplicationOptInTransaction>().Subject;
        payment.Amount.Should().Be(1_002_000);
        payment.Receiver.ToString().Should().Be(WttAlgorandStorage.Address(ulong.MaxValue, ulong.MaxValue, "guardian"u8));
        optIn.Sender.Should().Be(payment.Receiver);
        optIn.ApplicationId.Should().Be(ulong.MaxValue);
        optIn.RekeyTo.Should().Be(Address.ForApplication(ulong.MaxValue));
        optIn.Fee.Should().Be(0);
        var protocol = WttAlgorandStorageWire.ProtocolEnvelope(input);
        protocol.LSig.Logic.Should().Equal(WttAlgorandStorage.Program(input.Application, input.Index, "guardian"u8));
        protocol.LSig.Args.Should().BeNull();
        protocol.LSig.Sig.Should().BeNull();
        protocol.LSig.Msig.Should().BeNull();
        var signed = new[] { Encode(transactions[0].Sign(payer)), Encode(protocol) };
        WttAlgorandStorageWire.ValidateSigned(input, signed).Select(item => item.Tx.TxID())
            .Should().Equal(transactions.Select(item => item.TxID()));
    }

    [Theory]
    [InlineData("funding")]
    [InlineData("application")]
    [InlineData("rekey")]
    [InlineData("fee")]
    [InlineData("program")]
    [InlineData("logic-args")]
    [InlineData("delegation")]
    [InlineData("order")]
    [InlineData("trailing")]
    public void Frozen_group_rejects_changed_value_and_logic_authority(string mutation)
    {
        var payer = new AlgoAccount(); var input = Parameters(payer);
        var transactions = WttAlgorandStorageWire.Build(input);
        if (mutation == "funding") ((PaymentTransaction)transactions[0]).Amount++;
        var protocol = WttAlgorandStorageWire.ProtocolEnvelope(input);
        if (mutation == "application") ((ApplicationOptInTransaction)protocol.Tx).ApplicationId = 7;
        if (mutation == "rekey") protocol.Tx.RekeyTo = payer.Address;
        if (mutation == "fee") protocol.Tx.SetFee(1);
        if (mutation == "program") protocol.LSig.Logic[^1] ^= 1;
        if (mutation == "logic-args") protocol.LSig.Args = [new byte[] { 1 }];
        if (mutation == "delegation") protocol.LSig.Sig = new Signature(Enumerable.Repeat((byte)7, 64).ToArray());
        var envelopes = new[] { Encode(transactions[0].Sign(payer)), Encode(protocol) };
        if (mutation == "order") (envelopes[0], envelopes[1]) = (envelopes[1], envelopes[0]);
        if (mutation == "trailing") envelopes[1] = Convert.ToBase64String([..Convert.FromBase64String(envelopes[1]), 0]);
        Action validate = () => WttAlgorandStorageWire.ValidateSigned(input, envelopes);
        validate.Should().Throw<Exception>().Where(error => error is InvalidDataException || error is InvalidOperationException);
    }

    private static WttAlgorandStorageParameters Parameters(AlgoAccount payer) => new("bridge:storage:0", payer.Address.ToString(),
        ulong.MaxValue, ulong.MaxValue, Convert.ToHexStringLower("guardian"u8), 1_002_000, 2000, 1000,
        100, 200, Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray()), "localnet-v1");
    private static string Encode(SignedTransaction transaction) => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction));
}
