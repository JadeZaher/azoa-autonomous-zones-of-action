using System.Buffers.Binary;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandSourceWireTests
{
    [Theory]
    [InlineData("Transfer", 2)]
    [InlineData("Attest", 1)]
    public void Message_fee_payment_obeys_processor_relative_position(string stage, int feeOffset)
    {
        var signer = new AlgoAccount();
        var input = Parameters(signer) with { Stage = stage };
        var wire = WttAlgorandSourceWire.Build(input);
        var payment = wire[wire.Count - 1 - feeOffset].Should().BeOfType<PaymentTransaction>().Subject;
        payment.Amount.Should().Be(1234);
        payment.Receiver.ToString().Should().Be(Address.ForApplication(9).ToString());
        var call = wire[^1].Should().BeOfType<ApplicationNoopTransaction>().Subject;
        call.ApplicationId.Should().Be(9);
        BinaryPrimitives.ReadUInt64BigEndian(call.ApplicationArgs.ElementAt(1)).Should().Be(ulong.MaxValue);
        call.ForeignAssets.Should().Equal(ulong.MaxValue);
        call.ForeignApps.Should().Equal(7UL);
        var signed = Sign(wire, signer);
        WttAlgorandSourceWire.ValidateSigned(input, signed).Select(item => item.Tx.TxID())
            .Should().Equal(wire.Select(item => item.TxID()));
    }

    [Fact]
    public void Wrapped_transfer_routes_back_to_canonical_origin_storage_without_destroying_the_ASA()
    {
        var signer = new AlgoAccount();
        var input = Parameters(signer) with { Wrapped = true, OriginChain = 2, OriginAddressHex = new string('a', 64) };
        input = input with { AssetCreator = WttAlgorandStorage.Address(9, 2, Convert.FromHexString(input.OriginAddressHex)) };
        var wire = WttAlgorandSourceWire.Build(input);
        var transfer = wire[^2].Should().BeOfType<AssetTransferTransaction>().Subject;
        transfer.AssetReceiver.ToString().Should().Be(input.AssetCreator);
        transfer.AssetAmount.Should().Be(10000);
        wire.Should().NotContain(item => item is AssetConfigurationTransaction);
        Action attest = () => WttAlgorandSourceWire.Build(input with { Stage = "Attest" });
        attest.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("rekey")]
    [InlineData("fee")]
    [InlineData("order")]
    [InlineData("signature")]
    [InlineData("trailing")]
    public void Signed_group_cannot_mutate_value_authority_fees_or_framing(string mutation)
    {
        var signer = new AlgoAccount(); var input = Parameters(signer);
        var wire = WttAlgorandSourceWire.Build(input).ToArray();
        if (mutation == "recipient") ((AssetTransferTransaction)wire[^2]).AssetReceiver = signer.Address;
        if (mutation == "rekey") wire[0].RekeyTo = signer.Address;
        if (mutation == "fee") wire[0].SetFee(90000);
        if (mutation == "order") (wire[0], wire[1]) = (wire[1], wire[0]);
        var signed = Sign(wire, signer);
        if (mutation == "signature")
        {
            var envelope = AlgorandSignedEnvelope.DecodeCanonical(signed[0]); envelope.Sig.Bytes[0] ^= 1;
            signed[0] = Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(envelope));
        }
        if (mutation == "trailing") signed[0] = Convert.ToBase64String([.. Convert.FromBase64String(signed[0]), 0]);
        Action validate = () => WttAlgorandSourceWire.ValidateSigned(input, signed);
        validate.Should().Throw<Exception>().Where(error => error is InvalidDataException || error is InvalidOperationException);
    }

    private static WttAlgorandSourceParameters Parameters(AlgoAccount signer)
        => new("Transfer", "operation:source:transfer:0", signer.Address.ToString(), 7, 9, ulong.MaxValue,
            10000, 100, 2, new string('b', 64), false, 8, new string('0', 64), new AlgoAccount().Address.ToString(),
            1234, 500, 600, Convert.ToBase64String(new byte[32]), "local", 1000, 1000, 3000);

    private static string[] Sign(IEnumerable<Transaction> transactions, AlgoAccount signer)
        => transactions.Select(transaction => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction.Sign(signer)))).ToArray();
}
