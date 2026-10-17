using System.Buffers.Binary;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaPublicationTests
{
    [Fact]
    public void ReliableMessageUsesSubmissionClockAndFullWidthSequenceForGuardianBodyIdentity()
    {
        var intent = Intent(); var account = Message(intent);
        var publication = WttSolanaPublication.Read(account, intent, "Transfer", "signature", "slot:block", "message");
        Assert.Equal(uint.MaxValue, publication.Timestamp); Assert.Equal(ulong.MaxValue.ToString(), publication.Sequence);
        var body = Convert.FromHexString(publication.BodyHex);
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(body));
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(42)));
        var envelope = new byte[72 + body.Length]; envelope[0] = 1; envelope[5] = 1; body.CopyTo(envelope, 72);
        var parsed = WttVaaCodec.Parse(envelope);
        Assert.Equal(publication.BodyDigest, parsed.BodyDigest); Assert.Equal(publication.PayloadHex, parsed.PayloadHex);
        Assert.Equal(publication.EmitterAddress, parsed.EmitterAddress); Assert.Equal(publication.ConsistencyLevel, parsed.ConsistencyLevel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(45)]
    [InlineData(57)]
    [InlineData(59)]
    [InlineData(91)]
    [InlineData(100)]
    public void ChangedFramingEmitterOrEconomicPayloadCannotBecomeSourceEvidence(int offset)
    {
        var intent = Intent(); var account = Message(intent); account[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => WttSolanaPublication.Read(account, intent, "Transfer", "signature", "block", "message"));
    }

    [Fact]
    public void TrailingBytesAndAlternateRecipientAreRejected()
    {
        var intent = Intent(); var account = Message(intent);
        Assert.Throws<InvalidDataException>(() => WttSolanaPublication.Read(account.Concat(new byte[] { 0 }).ToArray(), intent, "Transfer", "sig", "block", "message"));
        Assert.Throws<InvalidDataException>(() => WttSolanaPublication.Read(account,
            intent with { RecipientProtocolAddress = new string('9', 64) }, "Transfer", "sig", "block", "message"));
    }

    private static byte[] Message(WttOperationIntent intent)
    {
        var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress, 1,
            intent.RecipientProtocolAddress, 2, intent.Amount.Fee));
        var bytes = new byte[95 + payload.Length]; "msg"u8.CopyTo(bytes); bytes[4] = 32;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(41), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(45), intent.Nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(49), ulong.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(57), 1);
        Convert.FromHexString(intent.Source.EmitterAddressHex).CopyTo(bytes, 59);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(91), (uint)payload.Length); payload.CopyTo(bytes, 95);
        return bytes;
    }

    private static WttOperationIntent Intent()
    {
        var source = new WttNetworkDescriptor("Solana", ChainNetwork.Devnet, "solana:genesis", "Devnet", 1,
            "core", "token", new string('1', 64), new(1, new string('2', 64), new string('3', 64), new string('4', 64)), new string('5', 64));
        var target = source with { ProtocolChain = 2, ChainType = "Ethereum" };
        var actor = Guid.NewGuid(); var authority = new WttSigningAuthority("payer", new SigningContext(actor, Guid.NewGuid(), false));
        return new(1, "operation", actor, null, source, target,
            new("mint", false, 9, 1, new string('6', 64), 9, new string('0', 64), new string('0', 64), "identity"),
            authority, authority, "recipient", new string('7', 64), null, 8,
            WttPayloadCodec.Normalize("12345", "20", 9), 29, 32, false, false,
            new("10000", "10000", "10000"), new("10000", "10000", "10000"));
    }
}
