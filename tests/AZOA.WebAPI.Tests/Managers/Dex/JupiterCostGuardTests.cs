using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterCostGuardTests
{
    [Fact]
    public void Guard_commits_exact_rent_and_both_executable_hashes_without_writable_authority()
    {
        var guard = new PublicKey(Enumerable.Repeat((byte)1, 32).ToArray());
        var rent = Enumerable.Repeat((byte)2, 17).ToArray();
        var jupiter = Enumerable.Repeat((byte)3, 32).ToArray();
        var whirlpool = Enumerable.Repeat((byte)4, 32).ToArray();
        var instruction = JupiterCostGuard.Build(guard, rent, jupiter, whirlpool);
        instruction.Data.Should().Equal(new byte[] { 4 }.Concat(rent).Concat(jupiter).Concat(whirlpool));
        instruction.Keys.Select(key => key.PublicKey).Should().Equal(SysVars.RentKey.Key, SysVars.ClockKey.Key,
            JupiterRouteAccounts.Program.Key, SolanaNativeLoader.ProgramData(JupiterRouteAccounts.Program).Key,
            WhirlpoolAccountState.Program.Key, SolanaNativeLoader.ProgramData(WhirlpoolAccountState.Program).Key);
        instruction.Keys.Should().OnlyContain(key => !key.IsSigner && !key.IsWritable);
        Action changedShape = () => JupiterCostGuard.Build(guard, [..rent, 0], jupiter, whirlpool);
        Action collision = () => JupiterCostGuard.Build(JupiterRouteAccounts.Program, rent, jupiter, whirlpool);
        changedShape.Should().Throw<InvalidDataException>();
        collision.Should().Throw<InvalidDataException>();
    }
}
