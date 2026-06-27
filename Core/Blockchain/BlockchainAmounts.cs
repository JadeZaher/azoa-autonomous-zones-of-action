using System.Globalization;
using System.Numerics;

namespace AZOA.WebAPI.Core.Blockchain;

/// <summary>
/// Chain-agnostic amount conversions between a chain's native/asset base units
/// (the integer chain-truth amount) and a decimal-adjusted display amount.
/// Extracted from <see cref="AZOA.WebAPI.Managers.WalletManager"/> so any
/// portfolio / faucet / transfer caller shares ONE overflow-safe implementation
/// instead of re-deriving the decimals math.
/// </summary>
public static class BlockchainAmounts
{
    /// <summary>The chain's native-coin decimal places (base units → whole coin).</summary>
    public static int NativeDecimalsFor(string chainType) => chainType.ToUpperInvariant() switch
    {
        "ALGORAND" or "ALGO" => 6,
        "SOLANA" or "SOL" => 9,
        "ETHEREUM" or "ETH" => 18,
        _ => 0
    };

    /// <summary>
    /// Converts a whole-coin display amount into a base-unit integer string
    /// (chain-truth raw amount), truncating any sub-base-unit remainder.
    /// <para>
    /// A large balance on an 18-decimal chain (ETH) scaled by <c>10^18</c> can
    /// exceed <see cref="decimal"/>'s ~7.9e28 range and throw
    /// <see cref="OverflowException"/>. We guard the multiply: on overflow we fall
    /// back to a <see cref="BigInteger"/> path — exact for the integer part,
    /// dropping only the sub-base-unit fraction (which truncation already discards
    /// anyway). Algorand's 6-decimal native amounts stay on the exact decimal path.
    /// </para>
    /// </summary>
    public static string ToBaseUnits(decimal displayAmount, int decimals)
    {
        if (displayAmount <= 0m || decimals <= 0)
            return decimal.Truncate(displayAmount < 0 ? 0 : displayAmount)
                .ToString(CultureInfo.InvariantCulture);

        try
        {
            var scaled = displayAmount;
            for (var i = 0; i < decimals; i++)
                scaled *= 10m;
            return decimal.Truncate(scaled).ToString(CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            // Decimal can't hold displayAmount * 10^decimals. Fall back to BigInteger:
            // multiply the truncated whole-coin part by 10^decimals exactly. We lose the
            // fractional part, but truncation discards it on the exact path too — so the
            // integer result matches, and a huge ETH balance no longer throws uncaught.
            var whole = decimal.Truncate(displayAmount);
            var bigWhole = new BigInteger(whole);
            var factor = BigInteger.Pow(10, decimals);
            return (bigWhole * factor).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Converts a base-unit integer string into a decimal-adjusted display amount,
    /// using <see cref="BigInteger"/> so arbitrarily large raw amounts (18-decimal
    /// chains, large supplies) never overflow. Returns the raw string unchanged when
    /// it can't be parsed.
    /// </summary>
    public static string FromBaseUnits(string rawAmount, int decimals)
    {
        if (string.IsNullOrWhiteSpace(rawAmount)
            || !BigInteger.TryParse(rawAmount, out var raw))
            return rawAmount ?? "0";

        if (decimals <= 0)
            return raw.ToString(CultureInfo.InvariantCulture);

        var factor = BigInteger.Pow(10, decimals);
        var whole = BigInteger.DivRem(raw, factor, out var fraction);
        if (fraction.IsZero)
            return whole.ToString(CultureInfo.InvariantCulture);

        var fracDigits = BigInteger.Abs(fraction)
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft(decimals, '0')
            .TrimEnd('0');
        return $"{whole.ToString(CultureInfo.InvariantCulture)}.{fracDigits}";
    }
}
