using System.Buffers.Binary;
using AZOA.WebAPI.Models.Wtt;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Exact core-owned target account contents; caller establishes owner, PDA and finalized snapshot.</summary>
internal static class WttSolanaPostedEvidence
{
    public static bool[] SignatureSet(byte[] account, WttVaa vaa, int guardianCount)
    {
        WttSolanaCoreInstructions.RequireCanonicalVaa(vaa);
        if (guardianCount is < 1 or > 19 || account.Length != 40 + guardianCount
            || BinaryPrimitives.ReadUInt32LittleEndian(account) != guardianCount
            || account.AsSpan(4, guardianCount).IndexOfAnyExcept((byte)0, (byte)1) >= 0
            || !account.AsSpan(4 + guardianCount, 32).SequenceEqual(WttSolanaCoreInstructions.BodyHash(vaa))
            || BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(36 + guardianCount)) != vaa.GuardianSetIndex)
            throw new InvalidDataException("Signature-set account differs from the exact body and guardian set.");
        return account.AsSpan(4, guardianCount).ToArray().Select(value => value == 1).ToArray();
    }

    public static (string SignatureSet, uint SubmissionTime) PostedVaa(byte[] account, WttVaa vaa)
    {
        WttSolanaCoreInstructions.RequireCanonicalVaa(vaa);
        var payload = Convert.FromHexString(vaa.PayloadHex);
        if (account.Length != 95 + payload.Length || !account.AsSpan(0, 3).SequenceEqual("vaa"u8)
            || account[3] != 1 || account[4] != vaa.ConsistencyLevel
            || BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(5)) != vaa.Timestamp
            || account.AsSpan(9, 32).IndexOfAnyExcept((byte)0) < 0
            || BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(45)) != vaa.Nonce
            || BinaryPrimitives.ReadUInt64LittleEndian(account.AsSpan(49)) != UInt64Text.Parse(vaa.Sequence)
            || BinaryPrimitives.ReadUInt16LittleEndian(account.AsSpan(57)) != vaa.EmitterChain
            || !account.AsSpan(59, 32).SequenceEqual(Convert.FromHexString(vaa.EmitterAddress))
            || BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(91)) != payload.Length
            || !account.AsSpan(95).SequenceEqual(payload))
            throw new InvalidDataException("Posted-VAA account differs from the exact canonical body.");
        return (new PublicKey(account.AsSpan(9, 32).ToArray()).Key, BinaryPrimitives.ReadUInt32LittleEndian(account.AsSpan(41)));
    }
}
