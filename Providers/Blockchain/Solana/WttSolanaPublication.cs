using System.Buffers.Binary;
using System.Globalization;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Reliable core message layout and guardian-compatible canonical publication body.</summary>
internal static class WttSolanaPublication
{
    public static WttPublication Read(byte[] account, WttOperationIntent intent, string stage,
        string transactionId, string blockIdentity, string messageAddress)
    {
        if (account.Length < 95 || !account.AsSpan(0, 3).SequenceEqual("msg"u8)
            || account[3] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(5)) != 0
            || account.AsSpan(9, 32).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Source publication is not a reliable unposted core message.");
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(91));
        if (payloadLength != account.Length - 95 || account[4] != intent.ConsistencyLevel
            || BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(45)) != intent.Nonce
            || BinaryPrimitives.ReadUInt16LittleEndian(account.AsSpan(57)) != 1
            || Convert.ToHexStringLower(account.AsSpan(59, 32)) != intent.Source.EmitterAddressHex)
            throw new InvalidDataException("Reliable publication framing or emitter differs from the frozen source intent.");
        var expected = stage switch
        {
            "Transfer" => WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress,
                intent.Asset.OriginChain, intent.RecipientProtocolAddress, intent.Target.ProtocolChain, intent.Amount.Fee)),
            "Attest" when intent.Asset.OriginChain == 1 => WttPayloadCodec.EncodeAttestation(new(intent.Asset.OriginAddress,
                1, intent.Asset.Decimals, intent.Asset.SymbolHex, intent.Asset.NameHex)),
            _ => throw new InvalidDataException("Publication stage has no matching original protocol payload."),
        };
        if (!account.AsSpan(95).SequenceEqual(expected)) throw new InvalidDataException("Source publication payload differs from the exact consented economics.");
        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(41));
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(account.AsSpan(49));
        var body = new byte[51 + expected.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, timestamp); BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), intent.Nonce);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), 1); account.AsSpan(59, 32).CopyTo(body.AsSpan(10));
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(42), sequence); body[50] = intent.ConsistencyLevel; expected.CopyTo(body, 51);
        return new(intent.Source.ProtocolIdentity, intent.Source.CoreBridge, intent.Source.TokenBridge, transactionId,
            blockIdentity, messageAddress, timestamp, intent.Nonce, 1, intent.Source.EmitterAddressHex,
            sequence.ToString(CultureInfo.InvariantCulture), intent.ConsistencyLevel, Convert.ToHexStringLower(expected),
            Convert.ToHexStringLower(body), Convert.ToHexStringLower(Keccak256.ComputeHash(Keccak256.ComputeHash(body))));
    }
}
