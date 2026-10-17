using System.Buffers.Binary;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class SolanaVersionedMessageTests
{
    [Fact]
    public void VersionZeroPreservesRolesAcrossMultipleTablesAndVerifiesExactDetachedSignatures()
    {
        var payer = new Account(); var second = new Account(); var writableA = new Account().PublicKey;
        var writableB = new Account().PublicKey; var readonlyA = new Account().PublicKey; var readonlyB = new Account().PublicKey;
        var program = new Account().PublicKey;
        var legacy = new TransactionBuilder().SetRecentBlockHash(new Account().PublicKey.Key).SetFeePayer(payer.PublicKey)
            .AddInstruction(new TransactionInstruction { ProgramId = program.KeyBytes, Data = [7, 9], Keys = new List<AccountMeta>
            {
                AccountMeta.Writable(payer.PublicKey, true), AccountMeta.ReadOnly(second.PublicKey, true),
                AccountMeta.Writable(writableA, false), AccountMeta.ReadOnly(readonlyA, false),
                AccountMeta.Writable(writableB, false), AccountMeta.ReadOnly(readonlyB, false),
            } }).CompileMessage();
        var tables = new[] { Table([readonlyA, writableA, payer.PublicKey, program]), Table([readonlyB, writableB, second.PublicKey]) };
        var compressed = SolanaMessageEnvelope.CompileVersionZero(legacy, tables, out var used);
        var parsed = SolanaMessageEnvelope.Read(compressed); var resolved = SolanaMessageEnvelope.Resolve(parsed, used);
        parsed.RequiredKeys.Select(key => key.Key).Should().Equal(payer.PublicKey.Key, second.PublicKey.Key);
        parsed.StaticKeys.Select(key => key.Key).Should().Contain(program.Key);
        resolved.Skip(parsed.StaticKeys.Count).Select(key => key.Key).Should().Equal(writableA.Key, writableB.Key, readonlyA.Key, readonlyB.Key);
        var original = SolanaMessageEnvelope.Read(legacy);
        parsed.Instructions[0].AccountIndices.Select(index => resolved[index].Key).Should()
            .Equal(original.Instructions[0].AccountIndices.Select(index => original.StaticKeys[index].Key));
        compressed.Length.Should().BeLessThan(legacy.Length);
        var signer = new SolanaTransactionSigner();
        using var payerKey = new SigningKeyMaterial(Encoders.Base58.DecodeData(payer.PrivateKey));
        using var secondKey = new SigningKeyMaterial(Encoders.Base58.DecodeData(second.PrivateKey));
        var first = signer.SignPartial(compressed, payer.PublicKey.Key, payerKey);
        var other = signer.SignPartial(compressed, second.PublicKey.Key, secondKey);
        first.IsError.Should().BeFalse(); other.IsError.Should().BeFalse();
        var wire = SolanaMessageEnvelope.Populate(compressed, [first.Result!, other.Result!]);
        SolanaMessageEnvelope.VerifyWire(wire, compressed).Should().Be(Encoders.Base58.EncodeData(first.Result!));
        var changed = compressed.ToArray(); changed[5 + parsed.StaticKeys.Count * 32] ^= 1;
        var wrongMessage = () => SolanaMessageEnvelope.VerifyWire(wire, changed);
        wrongMessage.Should().Throw<InvalidDataException>();
        var swapped = () => SolanaMessageEnvelope.Populate(compressed, [other.Result!, first.Result!]);
        swapped.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void OldSavedSnapshotCannotResolveAnIndexAddedInItsOwnSlotOrSubstituteChangedContents()
    {
        var payer = new Account(); var destination = new Account().PublicKey;
        var legacy = new TransactionBuilder().SetRecentBlockHash(new Account().PublicKey.Key).SetFeePayer(payer.PublicKey)
            .AddInstruction(SystemProgram.Transfer(payer.PublicKey, destination, 1)).CompileMessage();
        var saved = Table([destination]);
        var message = SolanaMessageEnvelope.CompileVersionZero(legacy, [saved], out var used);
        var sameSlot = used[0] with { Slot = 10 };
        var inactive = () => SolanaMessageEnvelope.Resolve(SolanaMessageEnvelope.Read(message), [sameSlot]);
        inactive.Should().Throw<InvalidDataException>();
        var savedKeys = SolanaMessageEnvelope.Resolve(SolanaMessageEnvelope.Read(message), used);
        var changedBytes = Convert.FromBase64String(saved.DataBase64);
        new Account().PublicKey.KeyBytes.CopyTo(changedBytes, 56);
        var changed = saved with { DataBase64 = Convert.ToBase64String(changedBytes) };
        SolanaMessageEnvelope.Resolve(SolanaMessageEnvelope.Read(message), [changed]).Select(key => key.Key)
            .Should().NotEqual(savedKeys.Select(key => key.Key));
        saved.ReadActiveAddresses()[0].Key.Should().Be(destination.Key);
    }

    [Fact]
    public void CanonicalFramingRejectsAliasesTrailingBytesUnknownVersionsAndLoadedSignerCollisions()
    {
        var payer = new Account(); var destination = new Account().PublicKey;
        var legacy = new TransactionBuilder().SetRecentBlockHash(new Account().PublicKey.Key).SetFeePayer(payer.PublicKey)
            .AddInstruction(SystemProgram.Transfer(payer.PublicKey, destination, 1)).CompileMessage();
        var message = SolanaMessageEnvelope.CompileVersionZero(legacy, [Table([destination])], out var used);
        var unknown = message.ToArray(); unknown[0] = 129;
        var alias = message.Take(4).Concat(new byte[] { (byte)(message[4] | 128), 0 }).Concat(message.Skip(5)).ToArray();
        foreach (var invalid in new[] { unknown, alias, message.Concat(new byte[] { 0 }).ToArray(), message[..^1] })
        {
            var read = () => SolanaMessageEnvelope.Read(invalid); read.Should().Throw<InvalidDataException>();
        }
        var collidedBytes = Convert.FromBase64String(used[0].DataBase64); payer.PublicKey.KeyBytes.CopyTo(collidedBytes, 56);
        var collision = () => SolanaMessageEnvelope.Resolve(SolanaMessageEnvelope.Read(message),
            [used[0] with { DataBase64 = Convert.ToBase64String(collidedBytes) }]);
        collision.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void TableOwnerDeactivationAndFutureSnapshotMetadataFailClosed()
    {
        var table = Table([new Account().PublicKey]);
        var wrongOwner = () => (table with { Owner = SystemProgram.ProgramIdKey.Key }).ReadActiveAddresses();
        wrongOwner.Should().Throw<InvalidDataException>();
        var bytes = Convert.FromBase64String(table.DataBase64); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), 9);
        var deactivating = () => (table with { DataBase64 = Convert.ToBase64String(bytes) }).ReadActiveAddresses();
        deactivating.Should().Throw<InvalidDataException>();
        var future = () => (table with { Slot = 9 }).ReadActiveAddresses(); future.Should().Throw<InvalidDataException>();
    }

    private static SolanaLookupTableSnapshot Table(IReadOnlyList<PublicKey> addresses)
    {
        var bytes = new byte[56 + addresses.Count * 32]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), 10);
        for (var index = 0; index < addresses.Count; index++) addresses[index].KeyBytes.CopyTo(bytes, 56 + index * 32);
        return new(new Account().PublicKey.Key, SolanaLookupTableSnapshot.Program, Convert.ToBase64String(bytes), 11);
    }
}
