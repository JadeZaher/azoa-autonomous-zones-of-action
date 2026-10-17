using System.Numerics;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Tinyman V2 fixed-input integer arithmetic from the pinned validator.</summary>
internal static class TinymanV2Math
{
    internal sealed record Quote(ulong Input, ulong Output, ulong TotalFee, ulong ProtocolFee, ulong PoolFee);

    internal static bool TryQuote(ulong input, ulong inputReserve, ulong outputReserve,
        ulong totalFeeShare, ulong protocolFeeRatio, out Quote? quote)
    {
        quote = null;
        if (input == 0 || inputReserve == 0 || outputReserve == 0 ||
            totalFeeShare is < 1 or > 100 || protocolFeeRatio is < 3 or > 10)
            return false;

        var totalFee = (BigInteger)input * totalFeeShare / 10_000;
        if (totalFee.IsZero || totalFee >= input)
            return false;
        var swapAmount = (BigInteger)input - totalFee;
        if ((BigInteger)inputReserve + swapAmount > ulong.MaxValue)
            return false;

        // The validator adds one even when the division has no remainder.
        var output = outputReserve - ((BigInteger)inputReserve * outputReserve / (inputReserve + swapAmount) + 1);
        var protocolFee = totalFee / protocolFeeRatio;
        if (output <= 0 || output >= outputReserve ||
            (BigInteger)inputReserve + input - protocolFee > ulong.MaxValue)
            return false;
        quote = new Quote(input, (ulong)output, (ulong)totalFee, (ulong)protocolFee, (ulong)(totalFee - protocolFee));
        return true;
    }
}
