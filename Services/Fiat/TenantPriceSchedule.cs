// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Fiat;

/// <summary>Lifecycle words for one registered price version. Mirrors the POCO allow-list.</summary>
public static class PriceScheduleStatuses
{
    /// <summary>Units may be bought at this price.</summary>
    public const string Active = "Active";

    /// <summary>The registrant has stopped selling units; the on-ramp refuses rather than reaching back.</summary>
    public const string Retired = "Retired";
}

/// <summary>
/// Who a price belongs to. A tenant's own compact id, or the operator's node-wide default.
/// </summary>
/// <remarks>
/// Kept as a plain string rather than a nullable tenant reference because the version sequence
/// is enforced by a UNIQUE index over (scope, chain, asset, version): a NONE column does not
/// participate in a unique index the way a value does, so the operator's own default would have
/// no single-winner version sequence at all.
/// </remarks>
public static class PriceScheduleScope
{
    /// <summary>The operator's node-wide default scope.</summary>
    public const string Node = TenantPriceSchedule.NodeScopeKey;

    /// <summary>The scope key one tenant's prices live under; <see cref="Node"/> when there is no tenant.</summary>
    public static string For(Guid? tenantId) =>
        tenantId is { } tenant && tenant != Guid.Empty ? tenant.ToString("N") : Node;

    /// <summary>True when <paramref name="scopeKey"/> names the operator's own default.</summary>
    public static bool IsNode(string? scopeKey) =>
        string.Equals(scopeKey?.Trim(), Node, StringComparison.Ordinal);
}

/// <summary>What a registrant sends to declare (or retire) the price money buys units at.</summary>
/// <remarks>
/// Deliberately no version field: the node assigns the version. A caller-chosen version is a
/// caller-chosen price history, and the whole point of the schedule is that an issuance can
/// name the exact row that priced it.
/// </remarks>
public sealed class PriceScheduleRegistration
{
    /// <summary>Chain the priced asset lives on, as the provider spells it ("Algorand").</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The asset the money buys. Required: a price with no asset prices nothing.</summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>ISO-4217 code of the money. USD only today (D15).</summary>
    public string CurrencyCode { get; set; } = PriceScheduleLimits.SupportedCurrencyCode;

    /// <summary>
    /// Base units of <see cref="AssetId"/> that ONE minor unit of <see cref="CurrencyCode"/>
    /// buys, as an exact decimal string. At two decimals, "1 unit = $0.01" is <c>"100"</c>.
    /// </summary>
    public string UnitsPerMinorUnit { get; set; } = string.Empty;

    /// <summary>The registrant's own name for where this price came from. Persisted verbatim.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Free-text operator note. Never interpreted.</summary>
    public string? Note { get; set; }

    /// <summary>When the registrant OBSERVED this price. Defaults to now; never the node's clock silently.</summary>
    public DateTimeOffset? DeclaredAt { get; set; }

    /// <summary>
    /// The version the caller believes is current. When set and the live sequence has moved on,
    /// the registration is REFUSED — so two operators cannot both "correct" a price and have the
    /// second one silently win.
    /// </summary>
    public long? ExpectedCurrentVersion { get; set; }

    /// <summary>Register this version as retired: the scope stops selling units at any price.</summary>
    public bool Retire { get; set; }
}

/// <summary>One registered price version, as the node holds it.</summary>
public sealed record RegisteredPriceSchedule(
    string RecordId,
    string ScopeKey,
    Guid? TenantId,
    string ChainType,
    string AssetId,
    string CurrencyCode,
    long Version,
    decimal UnitsPerMinorUnit,
    string Source,
    string? Note,
    string Status,
    DateTimeOffset DeclaredAt,
    DateTimeOffset RegisteredAt)
{
    /// <summary>True while units may actually be bought at this price.</summary>
    public bool IsActive => string.Equals(Status, PriceScheduleStatuses.Active, StringComparison.Ordinal);
}

/// <summary>The bounds a registered price and a converted payment have to stay inside.</summary>
public static class PriceScheduleLimits
{
    /// <summary>D15 pins the on-ramp to USD; another currency is refused, never converted.</summary>
    public const string SupportedCurrencyCode = "USD";

    /// <summary>Longest a registrant's source label may be.</summary>
    public const int MaxSourceLength = 128;

    /// <summary>Longest an operator note may be.</summary>
    public const int MaxNoteLength = 512;

    /// <summary>
    /// The largest rate the node will register. A fat-fingered extra zero on a money constant
    /// is the cheapest money-printer there is, and no honest price approaches this.
    /// </summary>
    public const decimal MaxUnitsPerMinorUnit = 1_000_000_000_000m;

    /// <summary>
    /// The largest payment the node will convert (one billion dollars in cents). Bounded so the
    /// conversion cannot overflow, and so a provider figure that is obviously not a card payment
    /// is refused rather than multiplied.
    /// </summary>
    public const long MaxAmountMinorUnits = 100_000_000_000L;

    /// <summary>How stale a declaration may be before the on-ramp refuses to price with it.</summary>
    public static readonly TimeSpan MaxDeclarationAge = TimeSpan.FromDays(30);

    /// <summary>Clock-skew grace before a declaration dated ahead of the payment is refused.</summary>
    public static readonly TimeSpan FutureDeclarationTolerance = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The one conversion on the on-ramp: money the provider confirmed → units of one asset.
/// See <c>Services/Fiat/OnRamp/AGENTS.md</c> §pricing.
/// </summary>
/// <remarks>
/// Every method here refuses rather than rounding a money figure into existence. The rate is
/// parsed with invariant culture from a string, never bound from a floating-point value; the
/// product is floored to whole base units (a fraction of a base unit cannot be issued and
/// rounding UP would create units nobody paid for); and a payment that converts to zero units
/// is refused outright, because taking money and issuing nothing is the failure this whole
/// item exists to make impossible.
/// </remarks>
public static class PriceScheduleMath
{
    /// <summary>Parses a declared rate, or explains why it is not one. Never returns a default.</summary>
    public static bool TryParseRate(string? raw, out decimal rate, out string error)
    {
        rate = 0m;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A price is required: state how many base units one minor unit of the currency buys.";
            return false;
        }

        if (!decimal.TryParse(
                raw.Trim(),
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            error = "The price is not a decimal number.";
            return false;
        }

        if (parsed <= 0m)
        {
            error = "The price must be greater than zero; a zero price would issue units for nothing.";
            return false;
        }

        if (parsed > PriceScheduleLimits.MaxUnitsPerMinorUnit)
        {
            error =
                $"The price exceeds the maximum this node will register "
                + $"({PriceScheduleLimits.MaxUnitsPerMinorUnit.ToString(CultureInfo.InvariantCulture)} base "
                + "units per minor unit). Check for a misplaced decimal point.";
            return false;
        }

        rate = parsed;
        return true;
    }

    /// <summary>
    /// Converts a provider-confirmed payment into whole base units at
    /// <paramref name="unitsPerMinorUnit"/>. Floors; refuses zero.
    /// </summary>
    public static bool TryConvert(
        long amountMinorUnits,
        decimal unitsPerMinorUnit,
        out BigInteger baseUnits,
        out string error)
    {
        baseUnits = BigInteger.Zero;
        error = string.Empty;

        if (amountMinorUnits <= 0)
        {
            error = "The payment total is not a positive amount of money.";
            return false;
        }

        if (amountMinorUnits > PriceScheduleLimits.MaxAmountMinorUnits)
        {
            error = "The payment total is larger than this node will convert.";
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
            product = decimal.Floor(amountMinorUnits * unitsPerMinorUnit);
        }
        catch (OverflowException)
        {
            error = "The payment total and the registered price do not produce a representable quantity.";
            return false;
        }

        if (product <= 0m)
        {
            // Money in, nothing out. Refusing here is what turns this into a visible, refundable
            // refusal instead of a person paying for zero units.
            error = "The payment is too small to buy a whole unit at the registered price.";
            return false;
        }

        var converted = new BigInteger(product);

        // The allocation seam carries an amount as an unsigned 64-bit quantity; a value it cannot
        // represent must be refused here, where the reason is still legible.
        if (converted > new BigInteger(ulong.MaxValue))
        {
            error = "The payment total and the registered price produce more units than one issuance can move.";
            return false;
        }

        baseUnits = converted;
        return true;
    }

    /// <summary>
    /// The D16 stamp direction: minor units of currency per ONE base unit of the asset — the
    /// reciprocal of the registered rate.
    /// </summary>
    /// <remarks>
    /// The authoritative direction is the registered one (units per minor unit): it is what
    /// actually converts money into units. This reciprocal is derived for the value stamp and is
    /// exact whenever the registered rate is (100 → 0.01); for a rate whose reciprocal does not
    /// terminate it carries decimal's precision, which is why the EXACT per-issuance truth is the
    /// <c>on_ramp_lot</c> row — it records the money and the units, from which the rate is
    /// re-derivable without this division.
    /// </remarks>
    public static bool TryDeriveMinorUnitsPerBaseUnit(decimal unitsPerMinorUnit, out decimal minorUnitsPerBaseUnit)
    {
        minorUnitsPerBaseUnit = 0m;
        if (unitsPerMinorUnit <= 0m)
            return false;

        try
        {
            minorUnitsPerBaseUnit = 1m / unitsPerMinorUnit;
        }
        catch (Exception exception) when (exception is DivideByZeroException or OverflowException)
        {
            return false;
        }

        return minorUnitsPerBaseUnit > 0m;
    }

    /// <summary>Round-trips a rate to the exact string form the schedule row stores.</summary>
    public static string FormatRate(decimal rate) => rate.ToString(CultureInfo.InvariantCulture);

    /// <summary>Round-trips a base-unit quantity to the exact string form value rows store.</summary>
    public static string FormatBaseUnits(BigInteger baseUnits) => baseUnits.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Validation of one registration, run before anything durable is written.</summary>
public static class PriceScheduleValidator
{
    /// <summary>Refusal text when this registration cannot be accepted, or null when it can.</summary>
    public static string? Validate(PriceScheduleRegistration? registration, out decimal rate)
    {
        rate = 0m;
        if (registration is null)
            return "A price registration is required.";

        if (string.IsNullOrWhiteSpace(registration.ChainType))
            return "ChainType is required.";
        if (string.IsNullOrWhiteSpace(registration.AssetId))
            return "AssetId is required: a price with no asset prices nothing.";
        if (string.IsNullOrWhiteSpace(registration.Source))
            return "Source is required: a price with no stated provenance cannot be audited.";
        if (registration.Source.Trim().Length > PriceScheduleLimits.MaxSourceLength)
            return $"Source must be {PriceScheduleLimits.MaxSourceLength} characters or fewer.";
        if (registration.Note is { } note && note.Trim().Length > PriceScheduleLimits.MaxNoteLength)
            return $"Note must be {PriceScheduleLimits.MaxNoteLength} characters or fewer.";

        var currency = registration.CurrencyCode?.Trim().ToUpperInvariant();
        if (!string.Equals(currency, PriceScheduleLimits.SupportedCurrencyCode, StringComparison.Ordinal))
        {
            return
                $"Only {PriceScheduleLimits.SupportedCurrencyCode} is supported today (D15); "
                + "a price in another currency is refused rather than converted.";
        }

        if (registration.ExpectedCurrentVersion is { } expected && expected < 0)
            return "ExpectedCurrentVersion cannot be negative.";

        return PriceScheduleMath.TryParseRate(registration.UnitsPerMinorUnit, out rate, out var rateError)
            ? null
            : rateError;
    }
}
