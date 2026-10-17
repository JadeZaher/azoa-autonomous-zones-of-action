using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Programs;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class SolanaRentGuardTests
{
    [Fact]
    public void RentOnlyFrameHasNoBridgeOrDexDependencies()
    {
        var rent = Enumerable.Range(0, 17).Select(value => (byte)value).ToArray();
        var guard = new Account().PublicKey;
        var instruction = SolanaRentGuard.Build(guard, rent);
        Assert.Equal(guard.KeyBytes, instruction.ProgramId); Assert.Equal(new byte[] { 6 }.Concat(rent), instruction.Data);
        var account = Assert.Single(instruction.Keys);
        Assert.Equal(SysVars.RentKey.Key, account.PublicKey); Assert.False(account.IsWritable); Assert.False(account.IsSigner);
        Assert.Throws<InvalidDataException>(() => SolanaRentGuard.Build(guard, rent[..16]));
        Assert.Throws<InvalidDataException>(() => SolanaRentGuard.Build(guard, [.. rent, 0]));
        Assert.Throws<InvalidDataException>(() => SolanaRentGuard.Build(SysVars.RentKey, rent));
    }
}
