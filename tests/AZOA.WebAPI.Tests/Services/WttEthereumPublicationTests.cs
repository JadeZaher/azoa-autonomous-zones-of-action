using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Services.Wormhole;
using Nethereum.Util;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttEthereumPublicationTests
{
    private const string Core = "0x1111111111111111111111111111111111111111";
    private const string Bridge = "0x2222222222222222222222222222222222222222";
    private static readonly string Transaction = "0x" + new string('3', 64);
    private static readonly string Block = "0x" + new string('4', 64);

    [Theory]
    [InlineData("exact", true)]
    [InlineData("sequence", false)]
    [InlineData("emitter", false)]
    [InlineData("transaction", false)]
    [InlineData("data", false)]
    public void RedemptionEventBindsFullWidthMessageIdentityAndExactReceipt(string mutation, bool accepted)
    {
        var envelope = new byte[72 + 51 + 133]; envelope[0] = 1; envelope[5] = 1;
        envelope[81] = 8; envelope[113] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(envelope.AsSpan(114), ulong.MaxValue);
        var vaa = WttVaaCodec.Parse(envelope);
        var topics = new[] { "0x" + new Sha3Keccack().CalculateHash("TransferRedeemed(uint16,bytes32,uint64)"),
            "0x" + new string('0', 63) + "8", "0x" + vaa.EmitterAddress, "0x" + new string('0', 48) + new string('f', 16) };
        if (mutation == "sequence") topics[3] = "0x" + new string('f', 64);
        if (mutation == "emitter") topics[2] = "0x" + new string('a', 64);
        var log = JsonSerializer.SerializeToElement(new { address = Bridge, transactionHash = mutation == "transaction" ? Block : Transaction,
            blockHash = Block, removed = false, topics, data = mutation == "data" ? "0x00" : "0x" });
        Assert.Equal(accepted, WttEthereumProtocol.IsRedemption(log, Bridge, Transaction, Block, vaa));
    }

    [Fact]
    public void ExactPublicationReconstructsCanonicalBodyWithFullUnsignedSequence()
    {
        var payload = Enumerable.Repeat((byte)7, 133).ToArray();
        Assert.True(WttEthereumProtocol.TryPublication(JsonSerializer.SerializeToElement(Log(payload)), "protocol", Core, Bridge,
            Transaction, Block, 42, 2, uint.MaxValue, 15, payload, out var publication));
        Assert.Equal(ulong.MaxValue.ToString(), publication!.Sequence);
        var body = Convert.FromHexString(publication.BodyHex);
        var envelope = new byte[72 + body.Length]; envelope[0] = 1; envelope[5] = 1; body.CopyTo(envelope, 72);
        var vaa = WttVaaCodec.Parse(envelope);
        Assert.Equal(publication.BodyDigest, vaa.BodyDigest);
        Assert.Equal(publication.EmitterAddress, vaa.EmitterAddress);
        Assert.Equal(publication.Sequence, vaa.Sequence);
        Assert.Equal(publication.PayloadHex, vaa.PayloadHex);
        Assert.Equal(publication.Nonce, vaa.Nonce);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("transactionHash")]
    [InlineData("blockHash")]
    [InlineData("removed")]
    [InlineData("sender")]
    [InlineData("extra-topic")]
    [InlineData("payload")]
    public void UnattributedOrChangedLogCannotBecomeExpectedPublication(string mutation)
    {
        var payload = new byte[100]; var log = Log(payload);
        if (mutation is "address" or "transactionHash" or "blockHash") log[mutation] = "0x" + new string('f', mutation == "address" ? 40 : 64);
        if (mutation == "removed") log["removed"] = true;
        if (mutation == "sender") ((string[])log["topics"])[1] = "0x" + new string('0', 24) + new string('1', 40);
        if (mutation == "extra-topic") log["topics"] = ((string[])log["topics"]).Append("0x" + new string('0', 64)).ToArray();
        if (mutation == "payload") payload[0] = 1;
        Assert.False(WttEthereumProtocol.TryPublication(JsonSerializer.SerializeToElement(log), "protocol", Core, Bridge,
            Transaction, Block, 42, 2, uint.MaxValue, 15, payload, out _));
    }

    [Fact]
    public void PublicationRejectsOffsetsPaddingOutOfRangeValuesAndTruncation()
    {
        var data = PublishedData(new byte[133]);
        Assert.True(WttEthereumProtocol.TryPublishedData(data, out var sequence, out var nonce, out var consistency, out _));
        Assert.Equal(ulong.MaxValue, sequence); Assert.Equal(uint.MaxValue, nonce); Assert.Equal((byte)15, consistency);
        foreach (var offset in new[] { 0, 32, 64, 96, 128, data.Length - 1 })
        {
            var altered = data.ToArray(); altered[offset] = 1;
            Assert.False(WttEthereumProtocol.TryPublishedData(altered, out _, out _, out _, out _));
        }
        for (var length = 0; length < data.Length; length++)
            Assert.False(WttEthereumProtocol.TryPublishedData(data.AsSpan(0, length), out _, out _, out _, out _));
        Assert.False(WttEthereumProtocol.TryPublishedData([.. data, 0], out _, out _, out _, out _));
    }

    [Fact]
    public void TransferCallPreservesFullUint256AndExactProtocolArguments()
    {
        var amount = (BigInteger.One << 256) - 1;
        var recipient = Enumerable.Repeat((byte)0xab, 32).ToArray();
        var call = WttEthereumProtocol.TransferCall(Bridge, amount, 8, recipient, 9, 10);
        var bytes = Convert.FromHexString(call[10..]);
        Assert.Equal(192, bytes.Length);
        Assert.Equal(amount, new BigInteger(bytes.AsSpan(32, 32), true, true));
        Assert.Equal(new BigInteger(8), new BigInteger(bytes.AsSpan(64, 32), true, true));
        Assert.True(bytes.AsSpan(96, 32).SequenceEqual(recipient));
        Assert.Equal(new BigInteger(9), new BigInteger(bytes.AsSpan(128, 32), true, true));
        Assert.Equal(new BigInteger(10), new BigInteger(bytes.AsSpan(160, 32), true, true));
        Assert.Throws<ArgumentException>(() => WttEthereumProtocol.TransferCall(Bridge, amount + 1, 8, recipient, 0, 10));
        Assert.Throws<ArgumentException>(() => WttEthereumProtocol.TransferCall(Bridge, 1, 8, recipient, 2, 10));
    }

    private static Dictionary<string, object> Log(byte[] payload) => new()
    {
        ["address"] = Core, ["transactionHash"] = Transaction, ["blockHash"] = Block, ["logIndex"] = "0x7", ["removed"] = false,
        ["topics"] = new[] { "0x" + new Sha3Keccack().CalculateHash("LogMessagePublished(address,uint64,uint32,bytes,uint8)"),
            "0x" + new string('0', 24) + Bridge[2..] }, ["data"] = EthereumHex.Data(PublishedData(payload)),
    };

    private static byte[] PublishedData(byte[] payload)
    {
        var data = new byte[160 + (payload.Length + 31) / 32 * 32];
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(24), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(60), uint.MaxValue);
        data[95] = 128; data[127] = 15;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(156), (uint)payload.Length); payload.CopyTo(data, 160);
        return data;
    }
}
