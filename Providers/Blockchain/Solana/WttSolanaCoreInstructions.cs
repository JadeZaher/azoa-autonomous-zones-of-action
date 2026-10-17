using System.Buffers.Binary;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Closed core signature-verification and posted-VAA instruction accounts and Borsh data.</summary>
internal static class WttSolanaCoreInstructions
{
    private static readonly PublicKey SecpProgram = new("KeccakSecp256k11111111111111111111111111111");
    private static readonly PublicKey InstructionSysvar = new("Sysvar1nstructions1111111111111111111111111");

    public static PublicKey GuardianSet(PublicKey core, uint index)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, index);
        return WttSolanaAccounts.Derive(core, "GuardianSet"u8.ToArray(), bytes);
    }

    public static PublicKey PostedVaa(PublicKey core, WttVaa vaa)
        => WttSolanaAccounts.Derive(core, "PostedVAA"u8.ToArray(), BodyHash(vaa));

    public static (TransactionInstruction Secp, TransactionInstruction Verify) Verify(PublicKey core, PublicKey payer,
        PublicKey signatureSet, WttVaa vaa, IReadOnlyList<string> guardians, int start, int count, byte secpInstructionIndex)
    {
        RequireCanonicalVaa(vaa);
        if (guardians.Count is < 1 or > 19 || count is < 1 or > 7 || start < 0 || start > vaa.Signatures.Count - count
            || payer.Key == signatureSet.Key || secpInstructionIndex == byte.MaxValue)
            throw new InvalidDataException("Exact bounded guardian batch, distinct payer/signature-set and adjacent instruction position are required.");
        var normalizedGuardians = guardians.Select(value => WttPayloadCodec.FixedHex(value, 20)).ToArray();
        if (normalizedGuardians.Select(value => Convert.ToHexStringLower(value)).Distinct(StringComparer.Ordinal).Count() != guardians.Count)
            throw new InvalidDataException("Guardian set contains duplicate actual addresses.");
        var dataStart = 1 + count * 11; var messageOffset = dataStart + count * 85;
        var secp = new byte[messageOffset + 32]; secp[0] = (byte)count; BodyHash(vaa).CopyTo(secp, messageOffset);
        var verify = Enumerable.Repeat((byte)255, 20).ToArray(); verify[0] = 7;
        for (var index = 0; index < count; index++)
        {
            var signature = vaa.Signatures[start + index];
            if (signature.GuardianIndex >= guardians.Count || signature.RecoveryId > 1)
                throw new InvalidDataException("Guardian signature index or recovery identifier is invalid.");
            var signatureOffset = dataStart + index * 85; var addressOffset = signatureOffset + 65; var offsets = 1 + index * 11;
            BinaryPrimitives.WriteUInt16LittleEndian(secp.AsSpan(offsets), (ushort)signatureOffset); secp[offsets + 2] = secpInstructionIndex;
            BinaryPrimitives.WriteUInt16LittleEndian(secp.AsSpan(offsets + 3), (ushort)addressOffset); secp[offsets + 5] = secpInstructionIndex;
            BinaryPrimitives.WriteUInt16LittleEndian(secp.AsSpan(offsets + 6), (ushort)messageOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(secp.AsSpan(offsets + 8), 32); secp[offsets + 10] = secpInstructionIndex;
            WttPayloadCodec.FixedHex(signature.R, 32).CopyTo(secp, signatureOffset);
            WttPayloadCodec.FixedHex(signature.S, 32).CopyTo(secp, signatureOffset + 32); secp[signatureOffset + 64] = signature.RecoveryId;
            normalizedGuardians[signature.GuardianIndex].CopyTo(secp, addressOffset);
            verify[1 + signature.GuardianIndex] = (byte)index;
        }
        return (new() { ProgramId = SecpProgram.KeyBytes, Keys = [], Data = secp },
            new() { ProgramId = core.KeyBytes, Data = verify, Keys = new List<AccountMeta>
            {
                AccountMeta.Writable(payer, true), AccountMeta.ReadOnly(GuardianSet(core, vaa.GuardianSetIndex), false),
                AccountMeta.Writable(signatureSet, true), AccountMeta.ReadOnly(InstructionSysvar, false),
                AccountMeta.ReadOnly(SysVars.RentKey, false), AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
            } });
    }

    public static TransactionInstruction Post(PublicKey core, PublicKey payer, PublicKey signatureSet, WttVaa vaa)
    {
        RequireCanonicalVaa(vaa);
        var payload = Convert.FromHexString(vaa.PayloadHex); var data = new byte[61 + payload.Length];
        data[0] = 2; data[1] = 1; BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), vaa.GuardianSetIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6), vaa.Timestamp); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), vaa.Nonce);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), vaa.EmitterChain); Convert.FromHexString(vaa.EmitterAddress).CopyTo(data, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(48), UInt64Text.Parse(vaa.Sequence)); data[56] = vaa.ConsistencyLevel;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(57), (uint)payload.Length); payload.CopyTo(data, 61);
        return new() { ProgramId = core.KeyBytes, Data = data, Keys = new List<AccountMeta>
        {
            AccountMeta.ReadOnly(GuardianSet(core, vaa.GuardianSetIndex), false), AccountMeta.ReadOnly(WttSolanaAccounts.Derive(core, "Bridge"u8.ToArray()), false),
            AccountMeta.ReadOnly(signatureSet, false), AccountMeta.Writable(PostedVaa(core, vaa), false), AccountMeta.Writable(payer, true),
            AccountMeta.ReadOnly(SysVars.ClockKey, false), AccountMeta.ReadOnly(SysVars.RentKey, false), AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
        } };
    }

    public static byte[] BodyHash(WttVaa vaa) => Keccak256.ComputeHash(Convert.FromHexString(vaa.BodyHex));

    public static void RequireCanonicalVaa(WttVaa vaa)
    {
        var parsed = WttVaaCodec.Parse(Convert.FromHexString(vaa.EnvelopeHex));
        if (CanonicalJson.Hash(parsed) != CanonicalJson.Hash(vaa)) throw new InvalidDataException("VAA fields differ from its exact canonical envelope.");
    }
}
