using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Lossless WTT payload 1/2 framing and normalized integer economics.</summary>
public static class WttPayloadCodec
{
    private static readonly BigInteger MaximumAmount = (BigInteger.One << 256) - 1;

    public static WttTransferPayload ParseTransfer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 133 || payload[0] != 1)
            throw new FormatException("Expected exactly one WTT transfer payload (type 1).");
        var amount = ReadAmount(payload.Slice(1, 32));
        var fee = ReadAmount(payload.Slice(101, 32));
        ushort origin = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(65, 2));
        ushort target = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(99, 2));
        if (amount.IsZero || fee > amount || origin == 0 || target == 0)
            throw new FormatException("Invalid WTT amount, fee or chain identity.");
        return new(Text(amount), WttVaaCodec.Hex(payload.Slice(33, 32)), origin,
            WttVaaCodec.Hex(payload.Slice(67, 32)), target, Text(fee));
    }

    public static byte[] EncodeTransfer(WttTransferPayload transfer)
    {
        var amount = ParseAmount(transfer.Amount);
        var fee = ParseAmount(transfer.Fee);
        if (amount.IsZero || fee > amount || transfer.TokenChain == 0 || transfer.RecipientChain == 0)
            throw new FormatException("Invalid WTT amount, fee or chain identity.");
        var payload = new byte[133];
        payload[0] = 1;
        WriteAmount(amount, payload.AsSpan(1, 32));
        FixedHex(transfer.TokenAddress, 32).CopyTo(payload, 33);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(65, 2), transfer.TokenChain);
        FixedHex(transfer.Recipient, 32).CopyTo(payload, 67);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(99, 2), transfer.RecipientChain);
        WriteAmount(fee, payload.AsSpan(101, 32));
        return payload;
    }

    public static byte[] EncodeAttestation(WttAttestationPayload attestation)
    {
        if (attestation.TokenChain == 0) throw new FormatException("Asset origin chain cannot be zero.");
        var payload = new byte[100];
        payload[0] = 2;
        FixedHex(attestation.TokenAddress, 32).CopyTo(payload, 1);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(33, 2), attestation.TokenChain);
        payload[35] = attestation.Decimals;
        FixedHex(attestation.SymbolHex, 32).CopyTo(payload, 36);
        FixedHex(attestation.NameHex, 32).CopyTo(payload, 68);
        return payload;
    }

    public static WttAttestationPayload ParseAttestation(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 100 || payload[0] != 2)
            throw new FormatException("Expected exactly one WTT asset attestation (type 2).");
        ushort chain = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(33, 2));
        if (chain == 0) throw new FormatException("Asset origin chain cannot be zero.");
        return new(WttVaaCodec.Hex(payload.Slice(1, 32)), chain, payload[35],
            WttVaaCodec.Hex(payload.Slice(36, 32)), WttVaaCodec.Hex(payload.Slice(68, 32)));
    }

    public static WttNormalizedAmount Normalize(string requested, string requestedFee, byte sourceDecimals)
    {
        BigInteger amount = ParseAmount(requested), fee = ParseAmount(requestedFee);
        if (amount.IsZero || fee > amount) throw new FormatException("Invalid source amount or relayer fee.");
        BigInteger divisor = BigInteger.Pow(10, Math.Max(0, sourceDecimals - 8));
        BigInteger normalized = amount / divisor, normalizedFee = fee / divisor;
        if (normalized.IsZero) throw new FormatException("Transfer rounds to zero WTT units.");
        BigInteger debited = normalized * divisor;
        return new(requested, Text(debited), Text(amount - debited), Text(normalized),
            Text(normalizedFee), Text(normalized - normalizedFee));
    }

    public static string Denormalize(string normalized, byte targetDecimals)
    {
        BigInteger amount = ParseAmount(normalized) * BigInteger.Pow(10, Math.Max(0, targetDecimals - 8));
        if (amount > MaximumAmount) throw new FormatException("Target amount exceeds uint256.");
        return Text(amount);
    }

    public static BigInteger ParseAmount(string? text)
    {
        if (text is null || text.Length is 0 or > 78
            || !BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount < 0 || amount > MaximumAmount || Text(amount) != text)
            throw new FormatException("Expected canonical unsigned uint256 decimal text.");
        return amount;
    }

    internal static byte[] FixedHex(string text, int length)
    {
        if (text is null || text.Length != length * 2)
            throw new FormatException("Incorrect fixed-width hexadecimal identity.");
        var bytes = Convert.FromHexString(text);
        if (WttVaaCodec.Hex(bytes) != text) throw new FormatException("Hexadecimal identity must be lowercase without a prefix.");
        return bytes;
    }

    private static BigInteger ReadAmount(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: true);
    private static string Text(BigInteger amount) => amount.ToString(CultureInfo.InvariantCulture);
    private static void WriteAmount(BigInteger amount, Span<byte> target)
    {
        var bytes = amount.ToByteArray(isUnsigned: true, isBigEndian: true);
        bytes.CopyTo(target[(target.Length - bytes.Length)..]);
    }
}
