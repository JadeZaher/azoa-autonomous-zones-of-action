using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class SolanaPacketBudgetTests
{
    [Theory]
    [InlineData(1167, 1, true)]
    [InlineData(1168, 1, false)]
    [InlineData(1103, 2, true)]
    [InlineData(1104, 2, false)]
    [InlineData(79, 18, true)]
    [InlineData(80, 18, false)]
    [InlineData(1, 19, true)]
    [InlineData(16, 19, false)]
    [InlineData(1, 128, false)]
    [InlineData(1, 256, false)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(-1, 1, false)]
    [InlineData(int.MaxValue, 1, false)]
    public void IncludesSignatureEnvelopeWithoutIntegerOverflow(int messageLength, int signatures, bool expected)
        => SolanaPacketBudget.Fits(messageLength, signatures).Should().Be(expected);
}
