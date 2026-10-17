using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaCleanupAccountingTests
{
    [Fact]
    public void OriginalPrecreditAndLaterCreditsNeverIncreaseAttributedDeposit()
    {
        var attributed = WttSolanaCleanupAccounting.OriginalDeposit(400, 475, 200, 25, 175, false);
        Assert.Equal(75UL, attributed);
        var returned = WttSolanaCleanupAccounting.CurrentReturn(475, 700);
        Assert.Equal(700UL, returned);
        Assert.Equal(20UL, WttSolanaCleanupAccounting.FinalizedReturn(720, 20, 100, 795, 5, returned, attributed));
    }

    [Fact]
    public void FailedAtomicFundingHasNoOriginalDepositCredit()
    {
        Assert.Equal(0UL, WttSolanaCleanupAccounting.OriginalDeposit(50, 50, 200, 25, 175, true));
        Assert.Throws<InvalidDataException>(() => WttSolanaCleanupAccounting.OriginalDeposit(50, 51, 200, 25, 175, true));
    }

    [Fact]
    public void LowerUnexplainedBalanceOrExcessOriginalDeltaRefusesAttribution()
    {
        Assert.Throws<InvalidDataException>(() => WttSolanaCleanupAccounting.CurrentReturn(475, 474));
        Assert.Throws<InvalidDataException>(() => WttSolanaCleanupAccounting.OriginalDeposit(400, 576, 200, 25, 175, false));
        Assert.Throws<InvalidDataException>(() => WttSolanaCleanupAccounting.OriginalDeposit(400, 500, 200, 25, 99, false));
    }

    [Fact]
    public void FullWidthBalancesDoNotOverflowAndUnrelatedDebitIsRejected()
    {
        Assert.Equal(0UL, WttSolanaCleanupAccounting.FinalizedReturn(10, 0, ulong.MaxValue - 5, ulong.MaxValue, 5, 10, 10));
        Assert.Throws<InvalidDataException>(() => WttSolanaCleanupAccounting.FinalizedReturn(10, 0, 100, 104, 5, 10, 10));
        Assert.Throws<InvalidDataException>(() => WttSolanaCleanupAccounting.FinalizedReturn(10, 1, 100, 105, 5, 10, 10));
    }
}
