using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class TinymanSwapWireTests
{
    [Fact]
    public void CleanupClosesOnlyTheOwnersZeroHoldingAndCannotSubstituteACreatorDestination()
    {
        var account = new AlgoAccount();
        var parameters = Parameters(account) with { Stage = "CloseOptIn", InputAsset = 0, OutputAsset = ulong.MaxValue };
        var transaction = TinymanSwapWire.Build(parameters).Single().Should().BeOfType<AssetTransferTransaction>().Subject;
        transaction.AssetAmount.Should().Be(0);
        transaction.AssetReceiver.EncodeAsString().Should().Be(parameters.Owner);
        transaction.AssetCloseTo.EncodeAsString().Should().Be(parameters.Owner);
        transaction.Group.Should().BeNull();
        TinymanSwapWire.ValidateSigned(parameters, Sign([transaction], account)).Should().ContainSingle();
        transaction.AssetCloseTo = new AlgoAccount().Address;
        var validate = () => TinymanSwapWire.ValidateSigned(parameters, Sign([transaction], account));
        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CanonicalGroupBindsFullWidthAssetsExactNetInputAndAtomicFee()
    {
        var account = new AlgoAccount();
        var parameters = Parameters(account);
        var wire = TinymanSwapWire.Build(parameters);
        wire.Count.Should().Be(3);
        var input = wire[0].Should().BeOfType<AssetTransferTransaction>().Subject;
        input.XferAsset.Should().Be(ulong.MaxValue);
        input.AssetAmount.Should().Be(9900UL);
        input.AssetReceiver.EncodeAsString().Should().Be(TinymanV2PoolLocator.GetPoolAddress(123, ulong.MaxValue, 0));
        var call = wire[1].Should().BeOfType<ApplicationNoopTransaction>().Subject;
        call.ApplicationId.Should().Be(123UL);
        call.ForeignAssets.Should().Equal(ulong.MaxValue, 0UL);
        call.ApplicationArgs.Last().Should().Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0x23, 0x28 });
        var fee = wire[2].Should().BeOfType<AssetTransferTransaction>().Subject;
        fee.AssetAmount.Should().Be(100UL);
        fee.AssetReceiver.EncodeAsString().Should().Be(parameters.Treasury);
        wire.Select(transaction => Convert.ToBase64String(transaction.Group.Bytes)).Distinct().Should().ContainSingle();
        var signed = Sign(wire, account);
        TinymanSwapWire.ValidateSigned(parameters, signed).Select(envelope => envelope.Tx.TxID())
            .Should().Equal(wire.Select(transaction => transaction.TxID()));
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("rekey")]
    [InlineData("fee")]
    [InlineData("genesis")]
    [InlineData("group-order")]
    [InlineData("trailing")]
    [InlineData("signature")]
    public void BrowserMutationCannotReplaceAdmittedCanonicalGroup(string mutation)
    {
        var account = new AlgoAccount();
        var parameters = Parameters(account);
        var wire = TinymanSwapWire.Build(parameters).ToArray();
        if (mutation == "recipient") ((AssetTransferTransaction)wire[0]).AssetReceiver = account.Address;
        if (mutation == "rekey") wire[0].RekeyTo = account.Address;
        if (mutation == "fee") wire[0].SetFee(1001);
        if (mutation == "genesis") wire[0].GenesisHash = new Digest(Convert.ToBase64String(new byte[32]));
        if (mutation == "group-order") (wire[0], wire[2]) = (wire[2], wire[0]);
        var signed = Sign(wire, account);
        if (mutation == "trailing") signed[0] = Convert.ToBase64String(Convert.FromBase64String(signed[0]).Concat(new byte[] { 0 }).ToArray());
        if (mutation == "signature")
        {
            var envelope = AlgorandSignedEnvelope.DecodeCanonical(signed[0]);
            envelope.Sig.Bytes[0] ^= 1;
            signed[0] = Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(envelope));
        }
        var validate = () => TinymanSwapWire.ValidateSigned(parameters, signed);
        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OptInIsItsOwnZeroTransferAndDoesNotChargePlatformFee()
    {
        var account = new AlgoAccount();
        var parameters = Parameters(account) with { Stage = "OptIn", InputAsset = 0, OutputAsset = ulong.MaxValue };
        var transaction = TinymanSwapWire.Build(parameters).Should().ContainSingle().Which.Should().BeOfType<AssetTransferTransaction>().Subject;
        transaction.AssetAmount.Should().Be(0);
        transaction.AssetReceiver.EncodeAsString().Should().Be(parameters.Owner);
        transaction.XferAsset.Should().Be(ulong.MaxValue);
        transaction.Group.Should().BeNull();
        TinymanSwapWire.ValidateSigned(parameters, Sign([transaction], account)).Should().ContainSingle();
    }

    [Fact]
    public void NativeInputUsesPaymentAndExplicitZeroPolicyAddsNoFeeTransfer()
    {
        var account = new AlgoAccount();
        var parameters = Parameters(account) with { InputAsset = 0, OutputAsset = ulong.MaxValue, PlatformFee = 0, Treasury = "" };
        var wire = TinymanSwapWire.Build(parameters);
        wire.Count.Should().Be(2);
        wire[0].Should().BeOfType<PaymentTransaction>().Subject.Amount.Should().Be(parameters.GrossInput);
        TinymanSwapWire.ValidateSigned(parameters, Sign(wire, account)).Count.Should().Be(2);
    }

    private static string[] Sign(IReadOnlyList<Transaction> transactions, AlgoAccount account)
        => transactions.Select(transaction => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction.Sign(account)))).ToArray();

    private static TinymanWireParameters Parameters(AlgoAccount account)
        => new("Swap", "swap:fixture:Swap:0:immutable-intent", account.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(),
            123, ulong.MaxValue, 0, 10000, 100, 9000, 900, 1900,
            Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()), "fixture-v1", 1000, 1000, 2000);
}
