using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterNativeAccountTests
{
    [Fact]
    public void Dedicated_account_is_key_bound_and_cannot_close_an_arbitrary_holding()
    {
        var owner = new PublicKey(Enumerable.Repeat((byte)1, 32).ToArray()).Key;
        var plan = JupiterNativeAccount.Create(owner, "swap:one:0", 2039280, 10000);
        plan.Should().Be(JupiterNativeAccount.Create(owner, "swap:one:0", 2039280, 10000));
        plan.Address.Should().NotBe(JupiterNativeAccount.Create(owner, "swap:one:1", 2039280, 10000).Address);
        var before = plan.Before(owner, "swap:one:0");
        before.Should().HaveCount(3);
        var create = before[0];
        BinaryPrimitives.ReadUInt32LittleEndian(create.Data).Should().Be(3);
        new PublicKey(create.Data.AsSpan(4, 32).ToArray()).Key.Should().Be(owner);
        var seedLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(create.Data.AsSpan(36)));
        System.Text.Encoding.UTF8.GetString(create.Data, 44, seedLength).Should().Be(plan.Seed);
        BinaryPrimitives.ReadUInt64LittleEndian(create.Data.AsSpan(44 + seedLength)).Should().Be(2049280);
        BinaryPrimitives.ReadUInt64LittleEndian(create.Data.AsSpan(52 + seedLength)).Should().Be(165);
        new PublicKey(create.Data.AsSpan(60 + seedLength, 32).ToArray()).Key.Should().Be(Solnet.Programs.TokenProgram.ProgramIdKey.Key);
        create.Data.Length.Should().Be(92 + seedLength);
        create.Keys[0].PublicKey.Should().Be(owner);
        create.Keys[1].PublicKey.Should().Be(plan.Address);
        var close = plan.After(owner, "swap:one:0");
        close.Data.Should().Equal((byte)9);
        close.Keys.Select(account => account.PublicKey).Should().Equal(plan.Address, owner, owner);
        close.Keys[2].IsSigner.Should().BeTrue();
        Action changedAccount = () => (plan with { Address = owner }).After(owner, "swap:one:0");
        Action changedKey = () => plan.After(owner, "swap:two:0");
        Action overflow = () => JupiterNativeAccount.Create(owner, "swap:one:0", 1, ulong.MaxValue);
        changedAccount.Should().Throw<InvalidDataException>();
        changedKey.Should().Throw<InvalidDataException>();
        overflow.Should().Throw<OverflowException>();
    }
}
