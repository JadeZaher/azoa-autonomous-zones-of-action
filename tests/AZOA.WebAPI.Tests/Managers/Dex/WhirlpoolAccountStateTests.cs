using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class WhirlpoolAccountStateTests
{
    [Fact]
    public void PoolIdentityBindsTheActualFeeTierSeedAndBothMintRoles()
    {
        var (address, bytes) = Pool();
        var state = WhirlpoolAccountState.ReadPool(address, bytes);
        state.TickSpacing.Should().Be(64);
        state.FeeTierIndex.Should().Be(64);
        state.MintA.Should().NotBe(state.MintB);
        state.VaultA.Should().NotBe(state.VaultB);
        foreach (var offset in new[] { 0, 8, 40, 43, 101, 181 })
        {
            var changed = bytes.ToArray(); changed[offset] ^= 1;
            Action read = () => WhirlpoolAccountState.ReadPool(address, changed);
            read.Should().Throw<InvalidDataException>();
        }
        Action trailing = () => WhirlpoolAccountState.ReadPool(address, [.. bytes, 0]);
        trailing.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothTickLayoutsBindPoolStartPdaAndCanonicalInitialization(bool dynamic)
    {
        var (address, poolBytes) = Pool();
        var pool = WhirlpoolAccountState.ReadPool(address, poolBytes);
        const int start = -5632;
        var bytes = new byte[dynamic ? 260 : 9988];
        Discriminator(dynamic ? "DynamicTickArray" : "TickArray").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), start);
        new PublicKey(address).KeyBytes.CopyTo(bytes, dynamic ? 12 : 9956);
        if (dynamic) { bytes[44] = 1; bytes[60] = 1; }
        else bytes[12] = 1;
        var tickAddress = WhirlpoolAccountState.TickArrayAddress(address, start);
        WhirlpoolAccountState.ReadTickArray(tickAddress, bytes, pool).Should().Be(start);
        foreach (var offset in new[] { 0, 8, dynamic ? 12 : 9956, dynamic ? 60 : 12 })
        {
            var changed = bytes.ToArray(); changed[offset] = offset == (dynamic ? 60 : 12) ? (byte)2 : (byte)(changed[offset] ^ 1);
            Action read = () => WhirlpoolAccountState.ReadTickArray(tickAddress, changed, pool);
            read.Should().Throw<InvalidDataException>();
        }
        Action truncation = () => WhirlpoolAccountState.ReadTickArray(tickAddress, bytes[..^1], pool);
        truncation.Should().Throw<InvalidDataException>();
        Action trailing = () => WhirlpoolAccountState.ReadTickArray(tickAddress, [.. bytes, 0], pool);
        trailing.Should().Throw<InvalidDataException>();
        if (dynamic)
        {
            bytes[44] = 0;
            Action mismatch = () => WhirlpoolAccountState.ReadTickArray(tickAddress, bytes, pool);
            mismatch.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void AdaptiveFeeTierSeedMayDifferFromTickSpacingWithoutChangingItsIdentity()
    {
        var (address, bytes) = Pool(1024);
        var pool = WhirlpoolAccountState.ReadPool(address, bytes);
        pool.TickSpacing.Should().Be(64);
        pool.FeeTierIndex.Should().Be(1024);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(43), 64);
        Action changed = () => WhirlpoolAccountState.ReadPool(address, bytes);
        changed.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DynamicTickBitmapCannotAddressAnUnrepresentedEightyNinthTick()
    {
        var (address, poolBytes) = Pool();
        var pool = WhirlpoolAccountState.ReadPool(address, poolBytes);
        var bytes = new byte[148];
        Discriminator("DynamicTickArray").CopyTo(bytes, 0);
        new PublicKey(address).KeyBytes.CopyTo(bytes, 12);
        bytes[55] = 1;
        Action read = () => WhirlpoolAccountState.ReadTickArray(WhirlpoolAccountState.TickArrayAddress(address, 0), bytes, pool);
        read.Should().Throw<InvalidDataException>();
    }

    internal static (string Address, byte[] Bytes) Pool(ushort feeTier = 64, string? mintA = null, string? mintB = null)
    {
        var bytes = new byte[653];
        Discriminator("Whirlpool").CopyTo(bytes, 0);
        Key(3).CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(41), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(43), feeTier);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(45), 3000);
        bytes[49] = 1; bytes[73] = 1;
        var firstMint = mintA is null ? Key(1) : new PublicKey(mintA).KeyBytes;
        var secondMint = mintB is null ? Key(2) : new PublicKey(mintB).KeyBytes;
        firstMint.CopyTo(bytes, 101); Key(4).CopyTo(bytes, 133);
        secondMint.CopyTo(bytes, 181); Key(5).CopyTo(bytes, 213);
        PublicKey.TryFindProgramAddress([Encoding.UTF8.GetBytes("whirlpool"), Key(3), firstMint, secondMint, bytes.AsSpan(43, 2).ToArray()],
            WhirlpoolAccountState.Program, out var address, out var bump).Should().BeTrue();
        bytes[40] = bump;
        return (address.Key, bytes);
    }

    private static byte[] Key(byte first) { var value = new byte[32]; value[0] = first; return value; }
    private static byte[] Discriminator(string type) => SHA256.HashData(Encoding.UTF8.GetBytes("account:" + type))[..8];
}
