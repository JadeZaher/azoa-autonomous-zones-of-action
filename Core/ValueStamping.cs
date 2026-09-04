// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;

namespace AZOA.WebAPI.Core;

/// <summary>
/// One observed conversion rate from a <see cref="ValueDimension"/> into a fiat
/// currency's minor unit, with the moment it was observed (decision D16).
/// </summary>
/// <remarks>
/// The rate is expressed as <b>minor units per one base unit</b> — USD cents per wei,
/// per micro-Algo, per SPL base unit — so stamping is one multiplication and the result
/// is already the minor-unit count that gets persisted. For chain assets this number is
/// tiny (a wei is ~3e-15 cents at $3000/ETH); <see cref="decimal"/> carries 28 significant
/// digits, and the multiplication itself is done in <see cref="BigInteger"/> so an
/// arbitrarily large amount cannot overflow the way <c>(decimal)someBigInteger</c> would.
/// <para>A quote is only ever produced by a real rate source. There is no default, no
/// zero-fallback and no "unknown" instance: an absent rate is the ABSENCE of a quote
/// (<c>null</c>), which is why <see cref="ValueStamp"/> is nullable on the record and a
/// stamped rate of <c>"0"</c> stays distinguishable from never having been priced.</para>
/// </remarks>
public sealed record ValueRateQuote
{
    public ValueRateQuote(
        ValueDimension currency,
        decimal minorUnitsPerBaseUnit,
        DateTimeOffset asOf,
        string source)
    {
        if (!currency.IsFiat)
            throw new ArgumentException(
                $"A value rate must convert INTO a fiat currency; got {currency}.", nameof(currency));
        if (minorUnitsPerBaseUnit < 0m)
            throw new ArgumentOutOfRangeException(
                nameof(minorUnitsPerBaseUnit), minorUnitsPerBaseUnit,
                "A conversion rate cannot be negative.");
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        Currency = currency;
        MinorUnitsPerBaseUnit = minorUnitsPerBaseUnit;
        AsOf = asOf;
        Source = source.Trim();
    }

    /// <summary>The fiat currency this quote converts into. USD only today (D15).</summary>
    public ValueDimension Currency { get; }

    /// <summary>Minor units of <see cref="Currency"/> per one base unit of the priced asset.</summary>
    public decimal MinorUnitsPerBaseUnit { get; }

    /// <summary>When the rate was observed — NOT when it was applied. Persisted so the figure stays re-derivable.</summary>
    public DateTimeOffset AsOf { get; }

    /// <summary>Provenance of the quote (the rate source's own identifier). Persisted for audit.</summary>
    public string Source { get; }
}

/// <summary>
/// The fiat value of one value-moving operation, frozen at the moment it settled
/// (decision D16). See <c>Persistence/SurrealDb/Models/AGENTS.md</c> §value-stamp.
/// </summary>
/// <remarks>
/// Both the computed figure AND the inputs that produced it are kept: reconstructing a
/// historical price after the fact is lossy and expensive, and a figure whose rate is
/// gone cannot be audited or re-derived. Every component is persisted as a string so no
/// column re-imposes a 64-bit or binary-floating-point ceiling on a financial number.
/// </remarks>
public sealed record ValueStamp
{
    private ValueStamp(
        string currencyCode,
        string minorUnits,
        string rate,
        DateTimeOffset rateAsOf,
        string rateSource)
    {
        CurrencyCode = currencyCode;
        MinorUnits = minorUnits;
        Rate = rate;
        RateAsOf = rateAsOf;
        RateSource = rateSource;
    }

    /// <summary>ISO-4217 code of the stamped currency ("USD"). Carried as data so a second currency is a value change (D15).</summary>
    public string CurrencyCode { get; }

    /// <summary>The value in <see cref="CurrencyCode"/>'s minor unit, as a canonical decimal string.</summary>
    public string MinorUnits { get; }

    /// <summary>The rate actually used, as an invariant decimal string. <c>"0"</c> is a real quoted rate; a missing stamp is the absent case.</summary>
    public string Rate { get; }

    /// <summary>When the applied rate was observed.</summary>
    public DateTimeOffset RateAsOf { get; }

    /// <summary>Provenance of the applied rate.</summary>
    public string RateSource { get; }

    /// <summary>Applies <paramref name="quote"/> to <paramref name="amount"/>, producing the stamp persisted on the operation row.</summary>
    public static ValueStamp From(RailAmount amount, ValueRateQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var minorUnits = ConvertBaseUnits(amount.BaseUnits, quote.MinorUnitsPerBaseUnit);
        return new ValueStamp(
            quote.Currency.Code,
            minorUnits.ToString(CultureInfo.InvariantCulture),
            quote.MinorUnitsPerBaseUnit.ToString(CultureInfo.InvariantCulture),
            quote.AsOf,
            quote.Source);
    }

    /// <summary>Rehydrates a stamp from its persisted columns; returns null when any component is absent (an unstamped row).</summary>
    public static ValueStamp? FromPersisted(
        string? currencyCode,
        string? minorUnits,
        string? rate,
        DateTimeOffset? rateAsOf,
        string? rateSource)
    {
        if (string.IsNullOrWhiteSpace(currencyCode)
            || string.IsNullOrWhiteSpace(minorUnits)
            || string.IsNullOrWhiteSpace(rate)
            || !rateAsOf.HasValue
            || string.IsNullOrWhiteSpace(rateSource))
        {
            return null;
        }

        return new ValueStamp(currencyCode, minorUnits, rate, rateAsOf.Value, rateSource);
    }

    /// <summary>
    /// <paramref name="baseUnits"/> × <paramref name="rate"/>, rounded half-up, entirely in
    /// integer arithmetic. The rate's own decimal mantissa and scale are read out of its bits
    /// rather than converting the amount to <see cref="decimal"/> — a base-unit count above
    /// ~7.9e28 would overflow that conversion, and a financial figure must not have a
    /// magnitude ceiling its own amount type does not have.
    /// </summary>
    internal static BigInteger ConvertBaseUnits(BigInteger baseUnits, decimal rate)
    {
        var bits = decimal.GetBits(rate);
        var scale = (bits[3] >> 16) & 0xFF;
        var mantissa = (new BigInteger((uint)bits[2]) << 64)
                     | (new BigInteger((uint)bits[1]) << 32)
                     | new BigInteger((uint)bits[0]);

        var numerator = baseUnits * mantissa;
        var denominator = BigInteger.Pow(10, scale);

        // Half-up on non-negative operands; both are non-negative by construction
        // (RailAmount refuses negatives, ValueRateQuote refuses negative rates).
        return ((numerator * 2) + denominator) / (denominator * 2);
    }
}
