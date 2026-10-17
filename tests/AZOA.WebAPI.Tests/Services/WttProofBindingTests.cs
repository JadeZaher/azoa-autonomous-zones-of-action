using System.Buffers.Binary;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttProofBindingTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("recipient", false)]
    [InlineData("amount", false)]
    [InlineData("emitter", false)]
    [InlineData("nonce", false)]
    [InlineData("parsed-body", false)]
    public void TransferBindsEnvelopeAndFrozenEconomicsWithoutClaimingSignatureVerification(string mutation, bool accepted)
    {
        var intent = Intent();
        var payload = new WttTransferPayload("100", new string('a', 64), 2, new string('b', 64), 8, "0");
        if (mutation == "recipient") payload = payload with { Recipient = new string('c', 64) };
        if (mutation == "amount") payload = payload with { Amount = "101" };
        var vaa = Envelope(WttPayloadCodec.EncodeTransfer(payload), mutation == "emitter" ? (ushort)1 : (ushort)2,
            mutation == "nonce" ? 8u : 7u);
        if (mutation == "parsed-body") vaa = vaa with { BodyDigest = new string('e', 64) };
        var proof = new WttVerifiedProof(vaa, null!);
        if (accepted) Assert.Equal(vaa.BodyDigest, WttProofBinding.Transfer(intent, proof).BodyDigest);
        else Assert.Throws<InvalidDataException>(() => WttProofBinding.Transfer(intent, proof));
    }

    [Fact]
    public void OriginAttestationRequiresOriginalEmitterAndActualPrecision()
    {
        var intent = Intent();
        var metadata = new WttAttestationPayload(intent.Asset.OriginAddress, 2, 18, new string('0', 64), new string('0', 64));
        var correct = new WttVerifiedProof(Envelope(WttPayloadCodec.EncodeAttestation(metadata), 2, 99), null!);
        Assert.Equal(correct.Vaa.BodyDigest, WttProofBinding.Attestation(intent, correct).BodyDigest);
        var representationAttestation = new WttVerifiedProof(Envelope(WttPayloadCodec.EncodeAttestation(metadata), 8, 99), null!);
        Assert.Throws<InvalidDataException>(() => WttProofBinding.Attestation(intent, representationAttestation));
        var wrongPrecision = new WttVerifiedProof(Envelope(WttPayloadCodec.EncodeAttestation(metadata with { Decimals = 8 }), 2, 99), null!);
        Assert.Throws<InvalidDataException>(() => WttProofBinding.Attestation(intent, wrongPrecision));
    }

    private static WttOperationIntent Intent()
    {
        var actor = Guid.NewGuid();
        var authority = new WttSigningAuthority("owner", new SigningContext(actor, Guid.NewGuid(), false));
        var source = new WttNetworkDescriptor("Ethereum", ChainNetwork.Devnet, "source", "Devnet", 2, "core", "token", "code",
            new(2, new string('a', 64), new string('c', 64), new string('e', 64)), new string('d', 64));
        var target = new WttNetworkDescriptor("Algorand", ChainNetwork.Devnet, "target", "Devnet", 8, "core", "token", "code",
            new(8, new string('a', 64), new string('c', 64), new string('e', 64)), new string('d', 64));
        return new(1, "operation", actor, null, source, target,
            new("asset", false, 18, 2, new string('a', 64), 18, new string('0', 64), new string('0', 64), "behavior"),
            authority, authority, "recipient", new string('b', 64), null, 8,
            WttPayloadCodec.Normalize("1000000000000", "0", 18), 7, 1, false, true, new("0", "0", "0"), new("0", "0", "0"));
    }

    private static WttVaa Envelope(byte[] payload, ushort emitter, uint nonce)
    {
        var wire = new byte[72 + 51 + payload.Length];
        wire[0] = 1; wire[5] = 1; wire[7] = 1; wire[39] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(76), nonce);
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(80), emitter);
        Convert.FromHexString(new string('d', 64)).CopyTo(wire, 82);
        BinaryPrimitives.WriteUInt64BigEndian(wire.AsSpan(114), 17);
        wire[122] = 1; payload.CopyTo(wire, 123);
        return WttVaaCodec.Parse(wire);
    }
}
