using System.Buffers.Binary;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Wormhole;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandTargetWireTests
{
    [Theory]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(19, 4)]
    public void Verification_splits_exact_signatures_six_per_stateless_call_with_no_empty_batch(int count, int batches)
    {
        var signer = new AlgoAccount(); var input = Parameters(signer, "Redeem", count);
        var wire = WttAlgorandTargetWire.Build(input);
        Assert.Equal(batches + 2, wire.Count);
        var signatureBytes = Convert.FromHexString(input.Proof.Vaa.EnvelopeHex).AsSpan(6, count * 66).ToArray();
        Assert.Equal(signatureBytes, wire.Take(batches).Cast<ApplicationNoopTransaction>()
            .SelectMany(transaction => transaction.ApplicationArgs[1]).ToArray());
        Assert.All(wire.Take(batches), transaction => { Assert.Equal(WttAlgorandTargetWire.VerifierAddress, transaction.Sender.ToString()); Assert.Equal(0UL, transaction.Fee); });
        var verified = WttAlgorandTargetWire.ValidateSigned(input, Sign(input, signer));
        Assert.Equal(wire.Select(transaction => transaction.TxID()), verified.Select(transaction => transaction.Tx.TxID()));
        var complete = Assert.IsType<ApplicationNoopTransaction>(wire[^1]);
        Assert.Equal("completeTransfer"u8.ToArray(), complete.ApplicationArgs[0]);
        Assert.Equal(input.Recipient, complete.Accounts[3].ToString());
        Assert.Equal(ulong.MaxValue, Assert.Single(complete.ForeignAssets));
    }

    [Fact]
    public void Registration_preserves_exact_verify_payment_two_distinct_nops_then_last_attestation_order()
    {
        var signer = new AlgoAccount(); var input = Parameters(signer, "Register", 7) with { Asset = 0 };
        var wire = WttAlgorandTargetWire.Build(input);
        Assert.Equal(7, wire.Count);
        Assert.Equal("verifyVAA"u8.ToArray(), Assert.IsType<ApplicationNoopTransaction>(wire[^5]).ApplicationArgs[0]);
        Assert.Equal(100000UL, Assert.IsType<PaymentTransaction>(wire[^4]).Amount);
        Assert.Equal(new byte[] { 1 }, Assert.IsType<ApplicationNoopTransaction>(wire[^3]).ApplicationArgs[1]);
        Assert.Equal(new byte[] { 2 }, Assert.IsType<ApplicationNoopTransaction>(wire[^2]).ApplicationArgs[1]);
        Assert.Equal("receiveAttest"u8.ToArray(), Assert.IsType<ApplicationNoopTransaction>(wire[^1]).ApplicationArgs[0]);
        Assert.Equal(wire.Count, wire.Select(transaction => transaction.TxID()).Distinct().Count());
        Assert.Equal(wire.Count, WttAlgorandTargetWire.ValidateSigned(input, Sign(input, signer)).Count);
    }

    [Theory]
    [InlineData("program")]
    [InlineData("guardian")]
    [InlineData("rekey")]
    [InlineData("fee")]
    [InlineData("payer")]
    [InlineData("order")]
    [InlineData("trailing")]
    public void Stateless_or_payer_wire_mutation_cannot_be_accepted(string mutation)
    {
        var signer = new AlgoAccount(); var input = Parameters(signer, "Redeem", 7);
        var envelopes = Sign(input, signer);
        var first = AlgorandSignedEnvelope.DecodeCanonical(envelopes[0]);
        if (mutation == "program") first = new(first.Tx, null, null, new LogicsigSignature([1, 2, 3]), null);
        if (mutation == "guardian") ((ApplicationCallTransaction)first.Tx).ApplicationArgs[2][0] ^= 1;
        if (mutation == "rekey") first.Tx.RekeyTo = signer.Address;
        if (mutation == "fee") first.Tx.SetFee(1000);
        envelopes[0] = Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(first));
        if (mutation == "payer") envelopes[^1] = Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(WttAlgorandTargetWire.Build(input)[^1].Sign(new AlgoAccount())));
        if (mutation == "order") (envelopes[0], envelopes[1]) = (envelopes[1], envelopes[0]);
        if (mutation == "trailing") envelopes[0] = Convert.ToBase64String([.. Convert.FromBase64String(envelopes[0]), 0]);
        Assert.ThrowsAny<Exception>(() => WttAlgorandTargetWire.ValidateSigned(input, envelopes));
    }

    [Fact]
    public void Wrong_proof_partition_or_fee_cap_refuses_before_building_payer_effects()
    {
        var input = Parameters(new AlgoAccount(), "Redeem", 7);
        Assert.Throws<InvalidDataException>(() => WttAlgorandTargetWire.Build(input with { CoreApplication = 8 }));
        Assert.Throws<InvalidDataException>(() => WttAlgorandTargetWire.Build(input with { VerificationFee = 2999 }));
        Assert.Throws<InvalidDataException>(() => WttAlgorandTargetWire.Build(input with { CompletionFee = 2999 }));
        Assert.Throws<InvalidDataException>(() => WttAlgorandTargetWire.Build(input with { Recipient = new AlgoAccount().Address.ToString() }));
    }

    private static string[] Sign(WttAlgorandTargetParameters input, AlgoAccount signer)
    {
        var wire = WttAlgorandTargetWire.Build(input); var batches = (input.Proof.Vaa.Signatures.Count + 5) / 6;
        return wire.Select((transaction, index) => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(index < batches
            ? WttAlgorandTargetWire.ProtocolEnvelope(input, index) : transaction.Sign(signer)))).ToArray();
    }

    private static WttAlgorandTargetParameters Parameters(AlgoAccount signer, string stage, int count)
    {
        var recipient = new Address(Enumerable.Repeat((byte)9, 32).ToArray());
        var payload = stage == "Register"
            ? WttPayloadCodec.EncodeAttestation(new(new string('c', 64), 2, 18, new string('0', 64), new string('0', 64)))
            : WttPayloadCodec.EncodeTransfer(new("10000", new string('c', 64), 2, Convert.ToHexStringLower(recipient.Bytes), 8, "100"));
        var envelope = new byte[6 + count * 66 + 51 + payload.Length]; envelope[0] = 1; envelope[5] = (byte)count;
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1), 4);
        for (var index = 0; index < count; index++) { envelope[6 + index * 66] = (byte)index; envelope[7 + index * 66] = 1; }
        var body = envelope.AsSpan(6 + count * 66); BinaryPrimitives.WriteUInt32BigEndian(body, 1800000000);
        BinaryPrimitives.WriteUInt16BigEndian(body[8..], 2); body.Slice(10, 32).Fill(0xbb);
        BinaryPrimitives.WriteUInt64BigEndian(body[42..], ulong.MaxValue); payload.CopyTo(body[51..]);
        var vaa = WttVaaCodec.Parse(envelope); var genesis = Convert.ToBase64String(new byte[32]);
        var guardians = Enumerable.Range(0, count).Select(index => index.ToString("x40")).ToArray();
        var evidence = new WttGuardianEvidence("algorand:" + genesis, "7", "9", 4, 4, guardians, 0,
            1800000001, "block", DateTimeOffset.UtcNow, 2, new string('b', 64));
        return new(stage, "operation:target:" + stage + ":0", signer.Address.ToString(), 7, 9, ulong.MaxValue,
            recipient.ToString(), new(vaa, evidence), 1000, 1000, (ulong)((count + 5) / 6 + 1) * 1000, 3000,
            500, 1500, genesis, "localnet-v1");
    }
}
