using System.Buffers.Binary;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaCoreInstructionTests
{
    [Fact]
    public void GuardianBatchOffsetsReferenceItsActualInstructionAfterGuardAndFunding()
    {
        var vaa = Vaa(); var guardians = Guardians();
        var (secp, verify) = WttSolanaCoreInstructions.Verify(Key(11), Key(12), Key(13), vaa, guardians, 1, 2, 4);
        Assert.Equal(2, secp.Data[0]); Assert.Equal(7, verify.Data[0]);
        Assert.Equal(0, verify.Data[1 + 3]); Assert.Equal(1, verify.Data[1 + 5]);
        Assert.All(Enumerable.Range(0, 19).Except([3, 5]), index => Assert.Equal(255, verify.Data[index + 1]));
        for (var index = 0; index < 2; index++)
        {
            var offset = 1 + index * 11;
            var signatureOffset = BinaryPrimitives.ReadUInt16LittleEndian(secp.Data.AsSpan(offset));
            var addressOffset = BinaryPrimitives.ReadUInt16LittleEndian(secp.Data.AsSpan(offset + 3));
            var messageOffset = BinaryPrimitives.ReadUInt16LittleEndian(secp.Data.AsSpan(offset + 6));
            Assert.Equal(4, secp.Data[offset + 2]); Assert.Equal(4, secp.Data[offset + 5]); Assert.Equal(4, secp.Data[offset + 10]);
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(secp.Data.AsSpan(offset + 8)));
            var signature = vaa.Signatures[index + 1];
            Assert.Equal(Convert.FromHexString(signature.R), secp.Data.AsSpan(signatureOffset, 32).ToArray());
            Assert.Equal(Convert.FromHexString(signature.S), secp.Data.AsSpan(signatureOffset + 32, 32).ToArray());
            Assert.Equal(signature.RecoveryId, secp.Data[signatureOffset + 64]);
            Assert.Equal(Convert.FromHexString(guardians[signature.GuardianIndex]), secp.Data.AsSpan(addressOffset, 20).ToArray());
            Assert.Equal(Keccak256.ComputeHash(Convert.FromHexString(vaa.BodyHex)), secp.Data.AsSpan(messageOffset, 32).ToArray());
        }
        Assert.Equal(Key(12).Key, verify.Keys[0].PublicKey);
        Assert.True(verify.Keys[0].IsSigner); Assert.True(verify.Keys[2].IsSigner);
        Assert.Equal(WttSolanaCoreInstructions.GuardianSet(Key(11), uint.MaxValue).Key, verify.Keys[1].PublicKey);
    }

    [Fact]
    public void PostUsesBorshLittleEndianFullWidthFieldsAndSingleHashPda()
    {
        var vaa = Vaa(); var core = Key(11);
        var post = WttSolanaCoreInstructions.Post(core, Key(12), Key(13), vaa);
        Assert.Equal(2, post.Data[0]); Assert.Equal(1, post.Data[1]);
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(post.Data.AsSpan(2)));
        Assert.Equal(vaa.Timestamp, BinaryPrimitives.ReadUInt32LittleEndian(post.Data.AsSpan(6)));
        Assert.Equal(vaa.Nonce, BinaryPrimitives.ReadUInt32LittleEndian(post.Data.AsSpan(10)));
        Assert.Equal(vaa.EmitterChain, BinaryPrimitives.ReadUInt16LittleEndian(post.Data.AsSpan(14)));
        Assert.Equal(Convert.FromHexString(vaa.EmitterAddress), post.Data.AsSpan(16, 32).ToArray());
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(post.Data.AsSpan(48)));
        Assert.Equal(vaa.ConsistencyLevel, post.Data[56]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(post.Data.AsSpan(57)));
        Assert.Equal(new byte[] { 1, 2, 3 }, post.Data.AsSpan(61).ToArray());
        var expected = WttSolanaAccounts.Derive(core, "PostedVAA"u8.ToArray(), Keccak256.ComputeHash(Convert.FromHexString(vaa.BodyHex)));
        var wrongDouble = WttSolanaAccounts.Derive(core, "PostedVAA"u8.ToArray(), Convert.FromHexString(vaa.BodyDigest));
        Assert.Equal(expected.Key, WttSolanaCoreInstructions.PostedVaa(core, vaa).Key);
        Assert.NotEqual(wrongDouble.Key, expected.Key); Assert.Equal(expected.Key, post.Keys[3].PublicKey);
        Assert.True(post.Keys[3].IsWritable); Assert.True(post.Keys[4].IsSigner);
    }

    [Fact]
    public void AlteredEnvelopeProjectionDuplicateGuardiansAndInvalidBatchAreRejected()
    {
        var vaa = Vaa(); var guardians = Guardians();
        Assert.Throws<InvalidDataException>(() => WttSolanaCoreInstructions.Post(Key(11), Key(12), Key(13), vaa with { Nonce = 0 }));
        guardians[1] = guardians[0].ToUpperInvariant();
        Assert.Throws<InvalidDataException>(() => WttSolanaCoreInstructions.Verify(Key(11), Key(12), Key(13), vaa, guardians, 0, 1, 4));
        Assert.Throws<InvalidDataException>(() => WttSolanaCoreInstructions.Verify(Key(11), Key(12), Key(13), vaa, Guardians(), 2, 2, 4));
        Assert.Throws<InvalidDataException>(() => WttSolanaCoreInstructions.Verify(Key(11), Key(12), Key(13), vaa, Guardians(), 0, 1, 255));
        Assert.Throws<InvalidDataException>(() => WttSolanaCoreInstructions.Verify(Key(11), Key(12), Key(12), vaa, Guardians(), 0, 1, 4));
    }

    [Fact]
    public void SignatureBitmapRequiresExactGuardianCountBodyAndCanonicalBooleans()
    {
        var vaa = Vaa(); var bytes = new byte[47]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 7);
        bytes[5] = 1; bytes[7] = 1; WttSolanaCoreInstructions.BodyHash(vaa).CopyTo(bytes, 11);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(43), vaa.GuardianSetIndex);
        Assert.Equal(new[] { false, true, false, true, false, false, false }, WttSolanaPostedEvidence.SignatureSet(bytes, vaa, 7));
        foreach (var offset in new[] { 0, 4, 11, 43 })
        {
            var changed = bytes.ToArray(); changed[offset] ^= 2;
            Assert.Throws<InvalidDataException>(() => WttSolanaPostedEvidence.SignatureSet(changed, vaa, 7));
        }
        Assert.Throws<InvalidDataException>(() => WttSolanaPostedEvidence.SignatureSet(bytes.Concat(new byte[] { 0 }).ToArray(), vaa, 7));
    }

    [Fact]
    public void PostedAccountBindsOriginalBodyAndKeepsTargetSubmissionClockSeparate()
    {
        var vaa = Vaa(); var bytes = new byte[98]; "vaa"u8.CopyTo(bytes); bytes[3] = 1; bytes[4] = vaa.ConsistencyLevel;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), vaa.Timestamp); Key(13).KeyBytes.CopyTo(bytes, 9);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(41), 456);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(45), vaa.Nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(49), ulong.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(57), vaa.EmitterChain);
        Convert.FromHexString(vaa.EmitterAddress).CopyTo(bytes, 59); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(91), 3);
        Convert.FromHexString(vaa.PayloadHex).CopyTo(bytes, 95);
        var posted = WttSolanaPostedEvidence.PostedVaa(bytes, vaa);
        Assert.Equal(Key(13).Key, posted.SignatureSet); Assert.Equal(456u, posted.SubmissionTime);
        foreach (var offset in new[] { 0, 3, 4, 5, 45, 49, 57, 59, 91, 95 })
        {
            var changed = bytes.ToArray(); changed[offset] ^= 1;
            Assert.Throws<InvalidDataException>(() => WttSolanaPostedEvidence.PostedVaa(changed, vaa));
        }
        Assert.Throws<InvalidDataException>(() => WttSolanaPostedEvidence.PostedVaa(bytes.Concat(new byte[] { 0 }).ToArray(), vaa));
    }

    [Fact]
    public void VerificationBatchesShareOnlyTheExactProofGenerationKeyAndKeepSeparatePayers()
    {
        var actor = Guid.NewGuid(); var wallet = Guid.NewGuid(); var vaa = Vaa();
        var authority = new WttSigningAuthority(Key(12).Key, new SigningContext(actor, wallet, false));
        var first = new WttPreparationBinding("solana:genesis", actor, null, "operation", "Target", "Verify:Transfer:0", 0, "intent", "quote");
        var second = first with { Stage = "Verify:Transfer:1", Generation = 2 };
        var keys1 = WttSolanaKeyRoles.Verification(first, authority, vaa, 0);
        var keys2 = WttSolanaKeyRoles.Verification(second, authority, vaa, 0);
        Assert.Equal(keys1[1], keys2[1]); Assert.NotEqual(keys1[0], keys2[0]);
        Assert.False(WttSolanaKeyRoles.Exact(keys1, keys2)); Assert.True(WttSolanaKeyRoles.Exact(keys2.Reverse(), keys2));
        var rotated = WttSolanaKeyRoles.Verification(second, authority, vaa, 1);
        Assert.NotEqual(keys2[1], rotated[1]); Assert.False(WttSolanaKeyRoles.Exact(rotated, keys2));
        Assert.False(WttSolanaKeyRoles.Exact([keys2[0], keys2[0]], keys2));
        Assert.False(WttSolanaKeyRoles.Exact([keys2[0], keys2[1] with { WalletId = Guid.NewGuid() }], keys2));
        var otherEnvelope = Convert.FromHexString(vaa.EnvelopeHex); otherEnvelope[^1] ^= 1;
        var otherProof = WttSolanaKeyRoles.Verification(second, authority, WttVaaCodec.Parse(otherEnvelope), 0);
        Assert.NotEqual(keys2[1], otherProof[1]);
        var changedGuardian = Convert.FromHexString(vaa.EnvelopeHex); changedGuardian[1] ^= 1;
        var rotatedGuardians = WttSolanaKeyRoles.Verification(second, authority, WttVaaCodec.Parse(changedGuardian), 0);
        Assert.NotEqual(keys2[1], rotatedGuardians[1]);
    }

    private static PublicKey Key(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
    private static string[] Guardians() => Enumerable.Range(0, 7).Select(index => Convert.ToHexStringLower(Enumerable.Repeat((byte)(0xaa + index), 20).ToArray())).ToArray();

    private static WttVaa Vaa()
    {
        var bytes = new byte[6 + 3 * 66 + 54]; bytes[0] = 1; bytes[5] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), uint.MaxValue);
        for (var index = 0; index < 3; index++)
        {
            var start = 6 + index * 66; bytes[start] = (byte)(1 + index * 2);
            bytes.AsSpan(start + 1, 32).Fill((byte)(index + 1)); bytes.AsSpan(start + 33, 32).Fill((byte)(index + 9));
            bytes[start + 65] = (byte)(index % 2);
        }
        var body = bytes.AsSpan(204); BinaryPrimitives.WriteUInt32BigEndian(body, uint.MaxValue - 1);
        BinaryPrimitives.WriteUInt32BigEndian(body[4..], 0xaabbccdd); BinaryPrimitives.WriteUInt16BigEndian(body[8..], 10002);
        body.Slice(10, 32).Fill(7); BinaryPrimitives.WriteUInt64BigEndian(body[42..], ulong.MaxValue);
        body[50] = 32; new byte[] { 1, 2, 3 }.CopyTo(body[51..]); return WttVaaCodec.Parse(bytes);
    }
}
