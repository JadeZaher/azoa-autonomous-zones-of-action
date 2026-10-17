using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Rpc.Builders;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterCompiledMessageTests
{
    [Fact]
    public void Version_zero_resolution_preserves_writable_and_readonly_roles()
    {
        var payer = Key(1); var writable = Key(2); var readOnly = Key(3);
        var instruction = new JupiterInstruction(JupiterRouteAccounts.Program.Key,
            [new(payer.Key, true, false), new(writable.Key, false, true), new(readOnly.Key, false, false)], [7]);
        var legacy = new TransactionBuilder().SetFeePayer(payer).SetRecentBlockHash(Key(4).Key)
            .AddInstruction(JupiterCompiledMessage.ToInstruction(instruction)).CompileMessage();
        var bytes = new byte[120];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), ulong.MaxValue);
        writable.KeyBytes.CopyTo(bytes, 56); readOnly.KeyBytes.CopyTo(bytes, 88);
        var table = new SolanaLookupTableSnapshot(Key(5).Key, SolanaLookupTableSnapshot.Program, Convert.ToBase64String(bytes), 11);
        var message = SolanaMessageEnvelope.CompileVersionZero(legacy, [table], out var used);
        SolanaMessageEnvelope.Read(message).Lookups.Should().ContainSingle();
        JupiterCompiledMessage.Validate(message, used, payer.Key, [instruction]);
        readOnly.KeyBytes.CopyTo(bytes, 56); writable.KeyBytes.CopyTo(bytes, 88);
        Action swappedTable = () => JupiterCompiledMessage.Validate(message, [table with { DataBase64 = Convert.ToBase64String(bytes) }], payer.Key, [instruction]);
        Action escalatedRole = () => JupiterCompiledMessage.Validate(message, used, payer.Key,
            [instruction with { Accounts = [new(payer.Key, true, false), new(writable.Key, false, true), new(readOnly.Key, false, true)] }]);
        swappedTable.Should().Throw<InvalidDataException>();
        escalatedRole.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Full_message_binds_instruction_order_payload_and_privilege_union()
    {
        var payer = Key(1);
        var other = Key(2);
        JupiterInstruction[] instructions =
        [
            new(SolanaComputeBudget.Program.Key, [], SolanaComputeBudget.Limit(200000).Data),
            new(JupiterRouteAccounts.Program.Key, [new(payer.Key, true, false), new(other.Key, false, true)], [1, 2, 3])
        ];
        var builder = new TransactionBuilder().SetFeePayer(payer).SetRecentBlockHash(Key(3).Key);
        foreach (var instruction in instructions) builder.AddInstruction(JupiterCompiledMessage.ToInstruction(instruction));
        var message = builder.CompileMessage();
        JupiterCompiledMessage.Validate(message, [], payer.Key, instructions);
        Action changedData = () => JupiterCompiledMessage.Validate(message, [], payer.Key,
            [instructions[0], instructions[1] with { Data = [1, 2, 4] }]);
        Action changedPrivilege = () => JupiterCompiledMessage.Validate(message, [], payer.Key,
            [instructions[0], instructions[1] with { Accounts = [new(payer.Key, true, false), new(other.Key, false, false)] }]);
        Action changedOrder = () => JupiterCompiledMessage.Validate(message, [], payer.Key, instructions.Reverse().ToArray());
        Action changedPayer = () => JupiterCompiledMessage.Validate(message, [], other.Key, instructions);
        Action trailing = () => JupiterCompiledMessage.Validate([..message, 0], [], payer.Key, instructions);
        changedData.Should().Throw<InvalidDataException>();
        changedPrivilege.Should().Throw<InvalidDataException>();
        changedOrder.Should().Throw<InvalidDataException>();
        changedPayer.Should().Throw<InvalidDataException>();
        trailing.Should().Throw<InvalidDataException>();
    }

    private static PublicKey Key(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
}
