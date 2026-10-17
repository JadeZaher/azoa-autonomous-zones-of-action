using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Wormhole token bridge calls and exact core publication decoding; see directory protocol notes.</summary>
internal static class WttEthereumProtocol
{
    internal const string Transfer = "transferTokens(address,uint256,uint16,bytes32,uint256,uint32)";
    internal const string NativeTransfer = "wrapAndTransferETH(uint16,bytes32,uint256,uint32)";
    internal const string Attest = "attestToken(address,uint32)";
    internal const string Register = "createWrapped(bytes)";
    internal const string Redeem = "completeTransfer(bytes)";
    internal const string RedeemNative = "completeTransferAndUnwrapETH(bytes)";
    private static readonly string PublishedTopic = "0x" + new Sha3Keccack().CalculateHash("LogMessagePublished(address,uint64,uint32,bytes,uint8)");

    internal static string TransferCall(string token, BigInteger amount, ushort targetChain, byte[] recipient, BigInteger fee, uint nonce,
        bool native = false)
    {
        if (!EthereumAddress.TryParse(token, out var address) || amount <= 0 || amount >= (BigInteger.One << 256)
            || fee < 0 || fee > amount || targetChain == 0 || recipient.Length != 32)
            throw new ArgumentException("Invalid bounded WTT transfer arguments.");
        return native ? EthereumAbi.EncodeCall(NativeTransfer, targetChain, recipient, fee, nonce)
            : EthereumAbi.EncodeCall(Transfer, address, amount, targetChain, recipient, fee, nonce);
    }

    internal static string AttestationCall(string token, uint nonce)
        => EthereumAddress.TryParse(token, out var address) ? EthereumAbi.EncodeCall(Attest, address, nonce)
            : throw new ArgumentException("WTT attestation requires an exact token address.");

    internal static string VaaCall(byte[] envelope, bool registration, bool unwrapNative = false)
    {
        if (envelope.Length is < 123 or > 65536 || registration && unwrapNative)
            throw new ArgumentException("Invalid bounded WTT proof call.");
        return EthereumAbi.EncodeCall(registration ? Register : unwrapNative ? RedeemNative : Redeem, envelope);
    }

    internal static bool IsRedemption(JsonElement log, string tokenBridge, string transactionId, string blockHash, WttVaa vaa)
    {
        if (log.ValueKind != JsonValueKind.Object || !Text(log, "address", out var address)
            || !EthereumAddress.AreSame(address, tokenBridge)
            || !Text(log, "transactionHash", out var tx) || !SameHex(tx, transactionId)
            || !Text(log, "blockHash", out var block) || !SameHex(block, blockHash)
            || !log.TryGetProperty("removed", out var removed) || removed.ValueKind != JsonValueKind.False
            || !log.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() != 4
            || topics.EnumerateArray().Any(topic => topic.ValueKind != JsonValueKind.String)
            || !Text(log, "data", out var data) || data != "0x") return false;
        return SameHex(topics[0].GetString()!, "0x" + new Sha3Keccack().CalculateHash("TransferRedeemed(uint16,bytes32,uint64)"))
            && SameHex(topics[1].GetString()!, "0x" + vaa.EmitterChain.ToString("x", CultureInfo.InvariantCulture).PadLeft(64, '0'))
            && SameHex(topics[2].GetString()!, "0x" + vaa.EmitterAddress)
            && SameHex(topics[3].GetString()!, "0x" + UInt64Text.Parse(vaa.Sequence).ToString("x", CultureInfo.InvariantCulture).PadLeft(64, '0'));
    }

    internal static bool TryPublication(JsonElement log, string protocol, string core, string tokenBridge,
        string transactionId, string blockHash, uint timestamp, ushort chain, uint expectedNonce,
        byte expectedConsistency, ReadOnlySpan<byte> expectedPayload, out WttPublication? publication)
    {
        publication = null;
        if (chain == 0 || log.ValueKind != JsonValueKind.Object
            || !Text(log, "address", out var address) || !EthereumAddress.AreSame(address, core)
            || !Text(log, "transactionHash", out var actualTransaction) || !SameHex(actualTransaction, transactionId)
            || !Text(log, "blockHash", out var actualBlock) || !SameHex(actualBlock, blockHash)
            || !Text(log, "logIndex", out var logIndex) || !EthereumHex.TryParseQuantity(logIndex, out var index) || index < 0
            || !log.TryGetProperty("removed", out var removed) || removed.ValueKind != JsonValueKind.False
            || !log.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() != 2
            || topics[0].ValueKind != JsonValueKind.String || !SameHex(topics[0].GetString()!, PublishedTopic)
            || topics[1].ValueKind != JsonValueKind.String || !EthereumHex.TryParseData(topics[1].GetString(), out var sender)
            || sender.Length != 32 || sender.AsSpan(0, 12).IndexOfAnyExcept((byte)0) >= 0
            || !EthereumAddress.AreSame(EthereumHex.Data(sender[12..]), tokenBridge)
            || !Text(log, "data", out var encoded) || !EthereumHex.TryParseData(encoded, out var data)
            || !TryPublishedData(data, out var sequence, out var nonce, out var consistency, out var payload)
            || nonce != expectedNonce || consistency != expectedConsistency || !payload.AsSpan().SequenceEqual(expectedPayload)) return false;
        var body = new byte[51 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), nonce);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), chain);
        sender.CopyTo(body, 10);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(42), sequence);
        body[50] = consistency; payload.CopyTo(body, 51);
        publication = new(protocol, core, tokenBridge, transactionId, blockHash, index.ToString(CultureInfo.InvariantCulture),
            timestamp, nonce, chain, Convert.ToHexStringLower(sender), sequence.ToString(CultureInfo.InvariantCulture), consistency,
            Convert.ToHexStringLower(payload), Convert.ToHexStringLower(body),
            Convert.ToHexStringLower(Keccak256.ComputeHash(Keccak256.ComputeHash(body))));
        return true;
    }

    internal static bool TryPublishedData(ReadOnlySpan<byte> data, out ulong sequence, out uint nonce,
        out byte consistency, out byte[] payload)
    {
        sequence = 0; nonce = 0; consistency = 0; payload = [];
        if (data.Length < 160 || data.Length > 65536 || data[..24].IndexOfAnyExcept((byte)0) >= 0
            || data.Slice(32, 28).IndexOfAnyExcept((byte)0) >= 0
            || new BigInteger(data.Slice(64, 32), true, true) != 128
            || data.Slice(96, 31).IndexOfAnyExcept((byte)0) >= 0) return false;
        var length = new BigInteger(data.Slice(128, 32), true, true);
        if (length < 0 || length > 65536 - 160) return false;
        var bytes = (int)length;
        var padded = (bytes + 31) / 32 * 32;
        if (data.Length != 160 + padded || data.Slice(160 + bytes, padded - bytes).IndexOfAnyExcept((byte)0) >= 0) return false;
        sequence = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(24, 8));
        nonce = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(60, 4)); consistency = data[127];
        payload = data.Slice(160, bytes).ToArray();
        return true;
    }

    private static bool Text(JsonElement value, string property, out string text)
    {
        text = "";
        if (!value.TryGetProperty(property, out var member) || member.ValueKind != JsonValueKind.String) return false;
        text = member.GetString()!;
        return true;
    }

    private static bool SameHex(string left, string right)
        => EthereumHex.TryParseData(left, out var a) && EthereumHex.TryParseData(right, out var b) && a.AsSpan().SequenceEqual(b);
}
