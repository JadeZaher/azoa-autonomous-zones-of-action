using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class WhirlpoolOracleStateTests
{
    [Fact]
    public void OracleBindsFullPackedLayoutPoolPdaAndFinalizedClock()
    {
        var (poolAddress, poolBytes) = WhirlpoolAccountStateTests.Pool(1024);
        var pool = WhirlpoolAccountState.ReadPool(poolAddress, poolBytes);
        var address = WhirlpoolAccountState.OracleAddress(poolAddress);
        var bytes = Oracle(pool);
        var oracle = WhirlpoolOracleState.Read(address, bytes, pool, 100);
        oracle.FilterPeriod.Should().Be(10);
        oracle.DecayPeriod.Should().Be(20);
        oracle.ControlFactor.Should().Be(1000);
        oracle.TickGroupReference.Should().Be(-1);
        foreach (var offset in new[] { 0, 8, 66, 110, 126, 253 })
        {
            var changed = bytes.ToArray(); changed[offset] ^= 1;
            Action read = () => WhirlpoolOracleState.Read(address, changed, pool, 100);
            read.Should().Throw<InvalidDataException>();
        }
        Action early = () => WhirlpoolOracleState.Read(address, bytes, pool, 99);
        early.Should().Throw<InvalidDataException>();
        Action trailing = () => WhirlpoolOracleState.Read(address, [.. bytes, 0], pool, 100);
        trailing.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(48, 0)]
    [InlineData(50, 10)]
    [InlineData(52, 10000)]
    [InlineData(62, 0)]
    [InlineData(62, 3)]
    [InlineData(62, 128)]
    [InlineData(64, 0)]
    [InlineData(64, 5633)]
    public void InvalidAdaptiveConstantsNeverBecomeAcceptedRouteState(int offset, ushort value)
    {
        var (address, poolBytes) = WhirlpoolAccountStateTests.Pool(1024);
        var pool = WhirlpoolAccountState.ReadPool(address, poolBytes);
        var bytes = Oracle(pool);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        Action read = () => WhirlpoolOracleState.Read(WhirlpoolAccountState.OracleAddress(address), bytes, pool, 100);
        read.Should().Throw<InvalidDataException>();
    }

    internal static byte[] Oracle(WhirlpoolPoolState pool)
    {
        var bytes = new byte[254];
        SHA256.HashData(Encoding.UTF8.GetBytes("account:Oracle"))[..8].CopyTo(bytes, 0);
        new PublicKey(pool.Address).KeyBytes.CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(50), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 5000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(54), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(58), 10000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(62), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(64), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(82), 99);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(90), 100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(102), -1);
        return bytes;
    }
}
