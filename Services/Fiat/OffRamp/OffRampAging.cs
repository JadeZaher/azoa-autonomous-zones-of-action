// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>How much of one on-ramp lot a cash-out drew down.</summary>
/// <param name="LotId">The <c>on_ramp_lot</c> record the units came from.</param>
/// <param name="BaseUnits">How many base units this cash-out took from it.</param>
public sealed record OnRampLotDraw(string LotId, BigInteger BaseUnits);

/// <summary>
/// The FIFO plan for one cash-out: which lots it consumes, and how much of it had no lot at all.
/// </summary>
/// <param name="Draws">Mature lots drawn down, oldest first.</param>
/// <param name="AgedBaseUnits">The part of the cash-out that came from a matured on-ramp lot.</param>
/// <param name="UnagedBaseUnits">
/// The part that came from units with NO on-ramp lot — units received for work or from selling
/// stake. They were never bought with reversible money, so nothing had to age before they could
/// leave (Q-X2).
/// </param>
public sealed record OffRampConsumptionPlan(
    IReadOnlyList<OnRampLotDraw> Draws, BigInteger AgedBaseUnits, BigInteger UnagedBaseUnits);

/// <summary>
/// What a person may actually cash out right now, and why the rest of their balance may not.
/// </summary>
/// <param name="HoldingBaseUnits">What their account holds on chain, read from the chain.</param>
/// <param name="LockedBaseUnits">
/// Units still inside the reversal window of the card payment that bought them.
/// </param>
/// <param name="CommittedBaseUnits">Units already promised to a cash-out that has not finished.</param>
/// <param name="NextUnlockAt">When the oldest locked lot matures, or null when nothing is locked.</param>
public sealed record CashOutAvailability(
    BigInteger HoldingBaseUnits,
    BigInteger LockedBaseUnits,
    BigInteger CommittedBaseUnits,
    DateTimeOffset? NextUnlockAt)
{
    /// <summary>What may leave today. Never negative: a shortfall is zero cashable, not a debt.</summary>
    public BigInteger CashableBaseUnits
    {
        get
        {
            var cashable = HoldingBaseUnits - LockedBaseUnits - CommittedBaseUnits;
            return cashable > BigInteger.Zero ? cashable : BigInteger.Zero;
        }
    }
}

/// <summary>
/// The source-aged cash-out rule: money that can still be taken back cannot leave the platform.
/// See <c>Services/Fiat/OffRamp/AGENTS.md</c> §aging.
/// </summary>
/// <remarks>
/// Total and side-effect free on purpose. This is the one piece of the off-ramp that decides
/// whether a person is allowed to take money out, so it has to be readable and testable without a
/// database, a chain or a clock.
/// </remarks>
public static class OffRampAging
{
    /// <summary>Parses a stored base-unit figure; a blank or unreadable value is zero.</summary>
    public static BigInteger ParseBaseUnits(string? raw)
        => BigInteger.TryParse(
            raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : BigInteger.Zero;

    /// <summary>Formats a base-unit figure the way every other value row stores one.</summary>
    public static string Format(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What is left of one lot after chargebacks and earlier cash-outs.</summary>
    public static BigInteger Remaining(OnRampLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var remaining = ParseBaseUnits(lot.QuantityBaseUnits) - ParseBaseUnits(lot.ConsumedBaseUnits);
        return remaining > BigInteger.Zero ? remaining : BigInteger.Zero;
    }

    /// <summary>
    /// True once the card payment that funded this lot can no longer be pulled back.
    /// </summary>
    /// <remarks>
    /// A lot with NO recorded window is never mature. The window is written from the rail's own
    /// declared <c>SettlementProfile</c>, so a missing one means the node could not establish when
    /// the money stops being reversible — and "unknown" must lock, not release. Treating it as
    /// mature would be a fail-open on the exact question this rule exists to answer.
    /// </remarks>
    public static bool IsMature(OnRampLot lot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lot);
        return lot.ReversalWindowEnds is { } windowEnds && windowEnds <= now;
    }

    /// <summary>
    /// Units that may not leave yet: everything still unspent inside a lot whose reversal window
    /// is open. Units with no lot never appear here, which is exactly why they are unaged.
    /// </summary>
    public static BigInteger LockedBaseUnits(IEnumerable<OnRampLot> issuedLots, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuedLots);

        var locked = BigInteger.Zero;
        foreach (var lot in issuedLots)
        {
            if (!IsMature(lot, now))
                locked += Remaining(lot);
        }

        return locked;
    }

    /// <summary>When the oldest still-locked lot matures, or null when nothing is locked.</summary>
    public static DateTimeOffset? NextUnlockAt(IEnumerable<OnRampLot> issuedLots, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuedLots);

        DateTimeOffset? earliest = null;
        foreach (var lot in issuedLots)
        {
            if (IsMature(lot, now) || Remaining(lot) <= BigInteger.Zero)
                continue;

            // A lot with no window has no unlock date to promise. Leaving `earliest` alone is the
            // honest answer: the caller shows "on hold" without inventing a date it cannot keep.
            if (lot.ReversalWindowEnds is not { } windowEnds)
                continue;

            if (earliest is null || windowEnds < earliest)
                earliest = windowEnds;
        }

        return earliest;
    }

    /// <summary>
    /// Draws <paramref name="quantityBaseUnits"/> from matured lots, oldest first, and reports
    /// what was left over as unaged.
    /// </summary>
    /// <remarks>
    /// <paramref name="issuedLots"/> must arrive in <c>issued_at</c> order — which is what
    /// <c>IOnRampLotLedger.ListIssuedForUserAsync</c> returns — because "oldest first" is the
    /// whole rule. Immature lots are skipped rather than partially drawn: a lot is either past
    /// its reversal window or it is not.
    /// </remarks>
    public static OffRampConsumptionPlan PlanConsumption(
        IReadOnlyList<OnRampLot> issuedLots, BigInteger quantityBaseUnits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuedLots);

        if (quantityBaseUnits <= BigInteger.Zero)
            return new OffRampConsumptionPlan(Array.Empty<OnRampLotDraw>(), BigInteger.Zero, BigInteger.Zero);

        var draws = new List<OnRampLotDraw>();
        var outstanding = quantityBaseUnits;

        foreach (var lot in issuedLots)
        {
            if (outstanding <= BigInteger.Zero)
                break;
            if (!IsMature(lot, now))
                continue;

            var remaining = Remaining(lot);
            if (remaining <= BigInteger.Zero)
                continue;

            var drawn = remaining < outstanding ? remaining : outstanding;
            draws.Add(new OnRampLotDraw(lot.Id, drawn));
            outstanding -= drawn;
        }

        return new OffRampConsumptionPlan(draws, quantityBaseUnits - outstanding, outstanding);
    }

    /// <summary>
    /// Units a cash-out must give up to be paid <paramref name="amountMinorUnits"/> at
    /// <paramref name="unitsPerMinorUnit"/>. Rounds UP.
    /// </summary>
    /// <remarks>
    /// Up, not down, and the direction is deliberate. The person names the MONEY they want; the
    /// node works out the units. Rounding down would pay a whole minor unit for a fraction of the
    /// units it is worth, and would do so on every cash-out — a slow leak out of the reserve. The
    /// over-charge is bounded by one base unit, which at the platform unit's two decimals is a
    /// hundredth of a unit.
    /// </remarks>
    public static bool TryQuoteUnits(
        long amountMinorUnits,
        decimal unitsPerMinorUnit,
        out BigInteger baseUnits,
        out string error)
    {
        baseUnits = BigInteger.Zero;
        error = string.Empty;

        if (amountMinorUnits <= 0)
        {
            error = "The amount to cash out is not a positive amount of money.";
            return false;
        }

        if (amountMinorUnits > PriceScheduleLimits.MaxAmountMinorUnits)
        {
            error = "The amount to cash out is larger than this node will convert.";
            return false;
        }

        if (unitsPerMinorUnit <= 0m || unitsPerMinorUnit > PriceScheduleLimits.MaxUnitsPerMinorUnit)
        {
            error = "The registered price is outside the range this node will apply.";
            return false;
        }

        decimal product;
        try
        {
            product = decimal.Ceiling(amountMinorUnits * unitsPerMinorUnit);
        }
        catch (OverflowException)
        {
            error = "The amount and the registered price do not produce a representable quantity.";
            return false;
        }

        if (product <= 0m)
        {
            error = "The amount is too small to be worth a whole unit at the registered price.";
            return false;
        }

        baseUnits = new BigInteger(product);
        return true;
    }

    /// <summary>
    /// The money <paramref name="baseUnits"/> is worth at <paramref name="unitsPerMinorUnit"/>.
    /// Rounds DOWN, for the mirror-image reason <see cref="TryQuoteUnits"/> rounds up.
    /// </summary>
    /// <remarks>
    /// This is the "how much can I cash out?" figure, not a settlement figure: the settlement
    /// quantity is always re-derived from the money through <see cref="TryQuoteUnits"/>, so the
    /// two can never disagree about what actually moves.
    /// </remarks>
    public static long QuoteMinorUnits(BigInteger baseUnits, decimal unitsPerMinorUnit)
    {
        if (baseUnits <= BigInteger.Zero || unitsPerMinorUnit <= 0m)
            return 0L;

        // The rate is turned into an exact integer fraction rather than divided as a decimal: a
        // holding can be far larger than decimal's range, and a rate like 0.5 must not be rounded
        // to 1 before it divides. A decimal IS a 96-bit integer plus a power-of-ten scale, so
        // reading both out of its bits gives numerator/10^scale with no arithmetic at all — and
        // BigInteger division truncates toward zero, which for two positive quantities is the
        // floor this needs.
        var bits = decimal.GetBits(unitsPerMinorUnit);
        var scale = (bits[3] >> 16) & 0xFF;
        var numerator =
            new BigInteger((uint)bits[0])
            + (new BigInteger((uint)bits[1]) << 32)
            + (new BigInteger((uint)bits[2]) << 64);
        if (numerator <= BigInteger.Zero)
            return 0L;

        var minorUnits = baseUnits * BigInteger.Pow(10, scale) / numerator;
        return minorUnits > new BigInteger(PriceScheduleLimits.MaxAmountMinorUnits)
            ? PriceScheduleLimits.MaxAmountMinorUnits
            : (long)minorUnits;
    }
}
