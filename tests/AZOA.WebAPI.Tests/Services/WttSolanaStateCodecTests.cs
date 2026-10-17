using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaStateCodecTests
{
    [Fact]
    public void TokenConfigSupportsExactMigratedPauseTailWithoutTreatingExpiryAsUnpaused()
    {
        var core = Enumerable.Repeat((byte)12, 32).ToArray();
        Assert.True(WttSolanaStateCodec.TokenConfig(core, core, out var legacyPaused)); Assert.False(legacyPaused);
        var migrated = new byte[137]; core.CopyTo(migrated, 0);
        Assert.True(WttSolanaStateCodec.TokenConfig(migrated, core, out var paused)); Assert.False(paused);
        migrated[32] = 1;
        Assert.True(WttSolanaStateCodec.TokenConfig(migrated, core, out paused)); Assert.True(paused);
        migrated[32] = 2; Assert.False(WttSolanaStateCodec.TokenConfig(migrated, core, out _));
        migrated[32] = 0;
        for (var length = 0; length < 137; length++)
            if (length != 32) Assert.False(WttSolanaStateCodec.TokenConfig(migrated.AsSpan(0, length), core, out _));
        Assert.False(WttSolanaStateCodec.TokenConfig([.. migrated, 0], core, out _));
        migrated[0] ^= 1; Assert.False(WttSolanaStateCodec.TokenConfig(migrated, core, out _));
    }

    [Fact]
    public void GuardianSetUsesExactLittleEndianLayoutAndFullUnsignedExpiry()
    {
        var data = Guardians();
        Assert.True(WttSolanaStateCodec.GuardianSet(data, uint.MaxValue, 200, out var keys, out var expiry));
        Assert.Equal(new[] { new string('1', 40), new string('2', 40) }, keys);
        Assert.Equal(uint.MaxValue, expiry);
        Assert.False(WttSolanaStateCodec.GuardianSet(data, 0, 200, out _, out _));
        Assert.False(WttSolanaStateCodec.GuardianSet(data, uint.MaxValue, 99, out _, out _));
        Assert.False(WttSolanaStateCodec.GuardianSet([.. data, 0], uint.MaxValue, 200, out _, out _));
        for (var length = 0; length < data.Length; length++)
            Assert.False(WttSolanaStateCodec.GuardianSet(data.AsSpan(0, length), uint.MaxValue, 200, out _, out _));
    }

    [Fact]
    public void GuardianSetRejectsDuplicateAndOversizedMembership()
    {
        var data = Guardians();
        data.AsSpan(8, 20).CopyTo(data.AsSpan(28, 20));
        Assert.False(WttSolanaStateCodec.GuardianSet(data, uint.MaxValue, 200, out _, out _));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), uint.MaxValue);
        Assert.False(WttSolanaStateCodec.GuardianSet(data, uint.MaxValue, 200, out _, out _));
    }

    [Fact]
    public void EndpointMustMatchExactChainAndEmitterWithoutTrailingData()
    {
        var emitter = Enumerable.Repeat((byte)42, 32).ToArray();
        var data = new byte[34]; BinaryPrimitives.WriteUInt16LittleEndian(data, 10002); emitter.CopyTo(data, 2);
        Assert.True(WttSolanaStateCodec.Endpoint(data, 10002, emitter));
        Assert.False(WttSolanaStateCodec.Endpoint(data, 2, emitter));
        Assert.False(WttSolanaStateCodec.Endpoint([.. data, 0], 10002, emitter));
        emitter[0]++;
        Assert.False(WttSolanaStateCodec.Endpoint(data, 10002, emitter));
    }

    [Fact]
    public void ClockMustBelongToSnapshotAndFitVaaTimeRange()
    {
        var data = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(data, ulong.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), uint.MaxValue);
        Assert.True(WttSolanaStateCodec.Clock(data, ulong.MaxValue, out var time));
        Assert.Equal(uint.MaxValue, time);
        Assert.False(WttSolanaStateCodec.Clock(data, 1, out _));
        Assert.False(WttSolanaStateCodec.Clock([.. data, 0], ulong.MaxValue, out _));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), -1);
        Assert.False(WttSolanaStateCodec.Clock(data, ulong.MaxValue, out _));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), (long)uint.MaxValue + 1);
        Assert.False(WttSolanaStateCodec.Clock(data, ulong.MaxValue, out _));
    }

    private static byte[] Guardians()
    {
        var data = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(data, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 2);
        data.AsSpan(8, 20).Fill(0x11); data.AsSpan(28, 20).Fill(0x22);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52), uint.MaxValue);
        return data;
    }
}
