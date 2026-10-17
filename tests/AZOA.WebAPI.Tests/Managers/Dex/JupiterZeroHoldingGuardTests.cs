using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterZeroHoldingGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_empty_holding_matches_execution_guard_and_rejects_value_or_foreign_authority(bool native)
    {
        var owner = Key(4);
        var mint = native ? new PublicKey("So11111111111111111111111111111111111111112") : Key(3);
        var data = new byte[165];
        mint.KeyBytes.CopyTo(data, 0); owner.KeyBytes.CopyTo(data, 32); data[108] = 1;
        if (native)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(109), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(113), 2039280);
        }
        JupiterZeroHoldingGuard.RequireEmpty(data, 2039280, mint.Key, owner.Key);
        var positive = data.ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(positive.AsSpan(64), 1);
        Action positiveValue = () => JupiterZeroHoldingGuard.RequireEmpty(positive, 2039280, mint.Key, owner.Key);
        positiveValue.Should().Throw<InvalidDataException>();
        var foreign = data.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(foreign.AsSpan(129), 1);
        Key(9).KeyBytes.CopyTo(foreign, 133);
        Action foreignClose = () => JupiterZeroHoldingGuard.RequireEmpty(foreign, 2039280, mint.Key, owner.Key);
        foreignClose.Should().Throw<InvalidDataException>();
        foreach (var offset in new[] { 72, 109, 129 })
        {
            var malformed = data.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(offset), 2);
            Action invalidOption = () => JupiterZeroHoldingGuard.RequireEmpty(malformed, 2039280, mint.Key, owner.Key);
            invalidOption.Should().Throw<InvalidDataException>();
        }
        if (native)
        {
            Action unsynchronized = () => JupiterZeroHoldingGuard.RequireEmpty(data, 2039281, mint.Key, owner.Key);
            unsynchronized.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void Frame_binds_exact_account_mint_owner_and_composed_close_privileges()
    {
        var guard = Key(1); var account = Key(2); var mint = Key(3); var owner = Key(4);
        var instruction = JupiterZeroHoldingGuard.Build(guard, account, mint, owner);
        instruction.ProgramId.Should().Equal(guard.KeyBytes);
        instruction.Data.Should().HaveCount(97);
        instruction.Data[0].Should().Be(5);
        instruction.Data[1..33].Should().Equal(account.KeyBytes);
        instruction.Data[33..65].Should().Equal(mint.KeyBytes);
        instruction.Data[65..97].Should().Equal(owner.KeyBytes);
        instruction.Keys.Should().HaveCount(2);
        instruction.Keys[0].PublicKey.Should().Be(account.Key);
        instruction.Keys[0].IsWritable.Should().BeTrue();
        instruction.Keys[0].IsSigner.Should().BeFalse();
        instruction.Keys[1].PublicKey.Should().Be(owner.Key);
        instruction.Keys[1].IsWritable.Should().BeTrue();
        instruction.Keys[1].IsSigner.Should().BeTrue();
        Action collision = () => JupiterZeroHoldingGuard.Build(guard, owner, mint, owner);
        collision.Should().Throw<InvalidDataException>();
    }

    private static PublicKey Key(byte marker) { var bytes = new byte[32]; bytes[0] = marker; return new(bytes); }
}
