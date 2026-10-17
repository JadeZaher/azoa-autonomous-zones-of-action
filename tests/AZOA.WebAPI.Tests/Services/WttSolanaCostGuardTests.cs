using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Programs;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaCostGuardTests
{
    [Fact]
    public void Metadata_only_frame_pins_rent_and_canonical_executable_without_bridge_accounts()
    {
        var rent = Enumerable.Range(0, 17).Select(value => (byte)value).ToArray();
        var hash = Enumerable.Repeat((byte)19, 32).ToArray();
        var instruction = WttSolanaCostGuard.BuildMetadata(Key(1), rent, hash);
        Assert.Equal(50, instruction.Data.Length); Assert.Equal((byte)3, instruction.Data[0]);
        Assert.Equal(rent, instruction.Data[1..18]); Assert.Equal(hash, instruction.Data[18..]);
        Assert.Equal(new[] { SysVars.RentKey.Key, SysVars.ClockKey.Key, SolanaNftMetadata.Program.Key,
            SolanaNativeLoader.ProgramData(SolanaNftMetadata.Program).Key }, instruction.Keys.Select(account => account.PublicKey));
        Assert.All(instruction.Keys, account => { Assert.False(account.IsSigner); Assert.False(account.IsWritable); });
        Assert.Throws<ArgumentException>(() => WttSolanaCostGuard.BuildMetadata(SolanaNftMetadata.Program, rent, hash));
        Assert.Throws<ArgumentException>(() => WttSolanaCostGuard.BuildMetadata(Key(1), rent[..16], hash));
    }

    [Fact]
    public void ClosedGuardWireBindsFullWidthFeeRentAndBothActualExecutableHashes()
    {
        var guard = Key(1); var core = Key(2); var token = Key(3);
        var rent = Enumerable.Range(1, 17).Select(value => (byte)value).ToArray();
        var coreHash = Enumerable.Repeat((byte)4, 32).ToArray(); var tokenHash = Enumerable.Repeat((byte)5, 32).ToArray();
        var instruction = WttSolanaCostGuard.Build(guard, core, token, ulong.MaxValue, rent, coreHash, tokenHash);
        Assert.Equal(90, instruction.Data.Length); Assert.Equal((byte)1, instruction.Data[0]);
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(instruction.Data.AsSpan(1)));
        Assert.Equal(rent, instruction.Data[9..26]); Assert.Equal(coreHash, instruction.Data[26..58]);
        Assert.Equal(tokenHash, instruction.Data[58..90]);
        Assert.Equal(new[] { WttSolanaAccounts.Derive(core, "Bridge"u8.ToArray()).Key, SysVars.RentKey.Key, SysVars.ClockKey.Key,
            core.Key, SolanaNativeLoader.ProgramData(core).Key, token.Key, SolanaNativeLoader.ProgramData(token).Key,
            WttSolanaAccounts.Derive(token, "config"u8.ToArray()).Key },
            instruction.Keys.Select(account => account.PublicKey));
        Assert.All(instruction.Keys, account => { Assert.False(account.IsSigner); Assert.False(account.IsWritable); });
        Assert.Throws<ArgumentException>(() => WttSolanaCostGuard.Build(core, core, token, 1, rent, coreHash, tokenHash));
        Assert.Throws<ArgumentException>(() => WttSolanaCostGuard.Build(guard, core, token, 1, rent[..16], coreHash, tokenHash));
    }

    private static PublicKey Key(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    [Fact]
    public void RegistrationExtensionPinsOnlyCanonicalMetadataWithExactVersionAndFrame()
    {
        var rent = new byte[17]; var coreHash = new byte[32]; var tokenHash = Enumerable.Repeat((byte)7, 32).ToArray();
        var metadataHash = Enumerable.Repeat((byte)8, 32).ToArray();
        var baseline = WttSolanaCostGuard.Build(Key(1), Key(2), Key(3), 41, rent, coreHash, tokenHash);
        var registration = WttSolanaCostGuard.BuildRegistration(Key(1), Key(2), Key(3), 41, rent, coreHash, tokenHash, metadataHash);
        Assert.Equal(122, registration.Data.Length); Assert.Equal((byte)2, registration.Data[0]);
        Assert.Equal(baseline.Data[1..], registration.Data[1..90]); Assert.Equal(metadataHash, registration.Data[90..]);
        Assert.Equal(10, registration.Keys.Count);
        Assert.Equal(baseline.Keys.Select(value => value.PublicKey), registration.Keys.Take(8).Select(value => value.PublicKey));
        Assert.Equal(SolanaNftMetadata.Program.Key, registration.Keys[8].PublicKey);
        Assert.Equal(SolanaNativeLoader.ProgramData(SolanaNftMetadata.Program).Key, registration.Keys[9].PublicKey);
        Assert.All(registration.Keys, value => { Assert.False(value.IsSigner); Assert.False(value.IsWritable); });
        Assert.Throws<ArgumentException>(() => WttSolanaCostGuard.BuildRegistration(Key(1), Key(2), Key(3), 41, rent, coreHash, tokenHash, metadataHash[..31]));
        Assert.Throws<ArgumentException>(() => WttSolanaCostGuard.BuildRegistration(Key(1), SolanaNftMetadata.Program, Key(3), 41, rent, coreHash, tokenHash, metadataHash));
        Assert.Equal((byte)1, baseline.Data[0]); Assert.Equal(8, baseline.Keys.Count);
    }

    [Fact]
    public void RentCalculationUsesFrozenSysvarWithProtocolIntegerThenFloatOrder()
    {
        var rent = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(rent, 3480);
        BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), 2.0); rent[16] = 50;
        Assert.Equal(2039280UL, WttSolanaCostGuard.MinimumBalance(rent, 165));
        Assert.Equal(890880UL, WttSolanaCostGuard.MinimumBalance(rent, 0));
        BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), double.NaN);
        Assert.Throws<ArgumentException>(() => WttSolanaCostGuard.MinimumBalance(rent, 165));
        BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), 2.0);
        BinaryPrimitives.WriteUInt64LittleEndian(rent, ulong.MaxValue);
        Assert.Throws<OverflowException>(() => WttSolanaCostGuard.MinimumBalance(rent, 165));
    }
}
