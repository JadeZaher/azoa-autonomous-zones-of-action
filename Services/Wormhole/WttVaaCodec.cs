using System.Buffers.Binary;
using System.Globalization;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Strict VAA v1 framing and canonical body identity; see AGENTS.md.</summary>
public static class WttVaaCodec
{
    public const int MaximumEnvelopeBytes = 65536;

    public static WttVaa Parse(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length is < 57 or > MaximumEnvelopeBytes || envelope[0] != 1)
            throw new FormatException("Expected a bounded Wormhole VAA v1 envelope.");
        int count = envelope[5];
        int bodyOffset = 6 + count * 66;
        if (count == 0 || envelope.Length < bodyOffset + 51)
            throw new FormatException("Missing signatures or truncated VAA body.");
        var signatures = new WttGuardianSignature[count];
        int previous = -1;
        for (int i = 0; i < count; i++)
        {
            var signature = envelope.Slice(6 + i * 66, 66);
            if (signature[0] <= previous || signature[65] > 1)
                throw new FormatException("Guardian indices must increase and recovery id must be 0 or 1.");
            previous = signature[0];
            signatures[i] = new(signature[0], Hex(signature.Slice(1, 32)), Hex(signature.Slice(33, 32)), signature[65]);
        }
        var body = envelope[bodyOffset..];
        ushort chain = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(8, 2));
        if (chain == 0) throw new FormatException("VAA emitter chain cannot be zero.");
        return new WttVaa(
            BinaryPrimitives.ReadUInt32BigEndian(envelope.Slice(1, 4)),
            Array.AsReadOnly(signatures),
            BinaryPrimitives.ReadUInt32BigEndian(body[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)),
            chain, Hex(body.Slice(10, 32)),
            BinaryPrimitives.ReadUInt64BigEndian(body.Slice(42, 8)).ToString(CultureInfo.InvariantCulture),
            body[50], Hex(body[51..]), Hex(body),
            Hex(Keccak256.ComputeHash(Keccak256.ComputeHash(body.ToArray()))), Hex(envelope));
    }

    public static WttVaa ParseBase64(string envelope)
    {
        if (string.IsNullOrEmpty(envelope) || envelope.Length > ((MaximumEnvelopeBytes + 2) / 3) * 4)
            throw new FormatException("Missing or oversized VAA envelope.");
        return Parse(Convert.FromBase64String(envelope));
    }

    internal static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
