// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;

namespace AZOA.WebAPI.Core;

/// <summary>What kind of unit a <see cref="RailAmount"/> is counted in.</summary>
public enum ValueDimensionKind
{
    /// <summary>A chain-native or chain-issued asset, counted in its smallest indivisible unit.</summary>
    ChainAsset = 0,

    /// <summary>A government-issued currency, counted in its minor unit (USD ⇒ cents).</summary>
    FiatCurrency = 1,
}

/// <summary>
/// The unit a <see cref="RailAmount"/> is denominated in — the "currency dimension"
/// half of the amount type (decision D13/D15). See <c>Core/AGENTS.md</c> §rail-amount.
/// </summary>
/// <remarks>
/// The dimension exists so a fiat amount can never be handed to a chain provider as
/// base units (or the reverse) without a provider noticing. Per D15 the fiat currency
/// is pinned to USD today, but it is carried as DATA (<see cref="Code"/>), so adding a
/// second currency later is a value change rather than a type change.
/// <para><c>default(ValueDimension)</c> is deliberately equal to
/// <see cref="NativeChainAsset"/>: a chain call names its asset in a sibling argument
/// (<c>tokenId</c>), so the common case must not require a second spelling of it.</para>
/// </remarks>
public readonly record struct ValueDimension
{
    private readonly string? _code;

    private ValueDimension(ValueDimensionKind kind, string? code)
    {
        Kind = kind;
        _code = code;
    }

    /// <summary>Whether this dimension counts a chain asset or a fiat currency.</summary>
    public ValueDimensionKind Kind { get; }

    /// <summary>
    /// The asset id (ASA id, SPL mint, ERC-20 contract) for a chain dimension, or the
    /// ISO-4217 code for a fiat one. Empty means "the unit named by the call's own
    /// asset argument", which is how every chain value call is expressed today.
    /// </summary>
    public string Code => _code ?? string.Empty;

    /// <summary>True when this amount is money on a fiat rail, not base units on a chain.</summary>
    public bool IsFiat => Kind == ValueDimensionKind.FiatCurrency;

    /// <summary>The base unit of whichever chain asset the call itself names. The default dimension.</summary>
    public static ValueDimension NativeChainAsset { get; } = new(ValueDimensionKind.ChainAsset, null);

    /// <summary>US dollars, counted in cents. The single fiat currency D15 pins today.</summary>
    public static ValueDimension Usd { get; } = new(ValueDimensionKind.FiatCurrency, "USD");

    /// <summary>Base units of one named chain asset. A blank id yields <see cref="NativeChainAsset"/>.</summary>
    public static ValueDimension ChainAsset(string? assetId) =>
        string.IsNullOrWhiteSpace(assetId)
            ? NativeChainAsset
            : new(ValueDimensionKind.ChainAsset, assetId.Trim());

    /// <summary>
    /// Minor units of a fiat currency. The code is upper-cased so <c>"usd"</c> and
    /// <c>"USD"</c> are the same dimension — two spellings of one currency would
    /// otherwise compare unequal and silently split a ledger.
    /// </summary>
    public static ValueDimension Fiat(string currencyCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        return new(ValueDimensionKind.FiatCurrency, currencyCode.Trim().ToUpperInvariant());
    }

    public override string ToString() => Kind switch
    {
        ValueDimensionKind.FiatCurrency => Code,
        _ => string.IsNullOrEmpty(Code) ? "base units" : $"base units of {Code}",
    };
}

/// <summary>
/// The amount type on every value-moving rail call: an arbitrary-precision base-unit
/// count plus the <see cref="ValueDimension"/> it is counted in (decisions D13 + D15).
/// Replaces the former <c>ulong</c> amount surface. See <c>Core/AGENTS.md</c> §rail-amount.
/// </summary>
/// <remarks>
/// The old <c>ulong</c> surface was chosen because Algorand's ASA <c>AssetAmount</c> is
/// ulong-native, but <see cref="ulong.MaxValue"/> is ~1.845e19 wei — about 18.45 ETH —
/// so the interface could not express an ordinary Ethereum holding at all. The backing
/// store is <see cref="BigInteger"/>; chains whose SDK genuinely takes a 64-bit amount
/// narrow at their own edge via <see cref="TryToUInt64"/>, which REFUSES rather than
/// truncating.
/// <para>Amounts are non-negative by construction: a negative movement is a different
/// operation (a refund/reversal), never a signed amount on the same call.</para>
/// </remarks>
public readonly record struct RailAmount : IComparable<RailAmount>
{
    private readonly BigInteger _baseUnits;

    private RailAmount(BigInteger baseUnits, ValueDimension dimension)
    {
        _baseUnits = baseUnits;
        Dimension = dimension;
    }

    /// <summary>The amount, counted in the smallest indivisible unit of <see cref="Dimension"/>.</summary>
    public BigInteger BaseUnits => _baseUnits;

    /// <summary>The unit <see cref="BaseUnits"/> is counted in.</summary>
    public ValueDimension Dimension { get; }

    /// <summary>True when this amount moves nothing.</summary>
    public bool IsZero => _baseUnits.IsZero;

    /// <summary>True when this amount is counted in chain base units (not fiat).</summary>
    public bool IsChainDenominated => Dimension.Kind == ValueDimensionKind.ChainAsset;

    /// <summary>Zero base units of the caller's own asset — the default value.</summary>
    public static RailAmount Zero => default;

    /// <summary>
    /// An amount in an explicit dimension. Throws on a negative count: a rail call that
    /// received one would move value in the direction opposite to the one it names.
    /// </summary>
    public static RailAmount In(ValueDimension dimension, BigInteger baseUnits)
    {
        if (baseUnits.Sign < 0)
            throw new ArgumentOutOfRangeException(
                nameof(baseUnits), baseUnits,
                "A rail amount cannot be negative; a reversal is a separate operation, not a signed amount.");

        return new RailAmount(baseUnits, dimension);
    }

    /// <summary>Base units of a chain asset. A blank <paramref name="assetId"/> means the call's own asset.</summary>
    public static RailAmount OfChainAsset(BigInteger baseUnits, string? assetId = null) =>
        In(ValueDimension.ChainAsset(assetId), baseUnits);

    /// <summary>US dollars in cents (D15). The only fiat currency minted today.</summary>
    public static RailAmount OfUsdMinorUnits(BigInteger minorUnits) =>
        In(ValueDimension.Usd, minorUnits);

    /// <summary>
    /// Lossless widening from the retired <c>ulong</c> surface, so a caller that already
    /// holds a 64-bit chain base-unit count keeps reading naturally. There is deliberately
    /// no implicit narrowing back — see <see cref="TryToUInt64"/>.
    /// </summary>
    public static implicit operator RailAmount(ulong baseUnits) => OfChainAsset(baseUnits);

    /// <summary>
    /// Parses a canonical decimal base-unit string (the form value amounts are persisted
    /// and transported in). Rejects blanks, signs, separators, and anything non-numeric.
    /// </summary>
    public static bool TryParse(string? text, ValueDimension dimension, out RailAmount amount)
    {
        amount = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (!BigInteger.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;

        amount = In(dimension, parsed);
        return true;
    }

    /// <summary>
    /// Narrows to the 64-bit surface a chain SDK may take (Algorand's ASA
    /// <c>AssetAmount</c>, an SPL token amount). Returns <c>false</c> — never a wrapped
    /// value — when the amount does not fit, and for any non-chain dimension: fiat cents
    /// interpreted as chain base units would move a completely different quantity.
    /// The caller phrases the refusal in its own chain's terms.
    /// </summary>
    public bool TryToUInt64(out ulong value)
    {
        value = 0;
        if (!IsChainDenominated)
            return false;
        if (_baseUnits.Sign < 0 || _baseUnits > ulong.MaxValue)
            return false;

        value = (ulong)_baseUnits;
        return true;
    }

    /// <summary>The same count, re-labelled into <paramref name="dimension"/>.</summary>
    public RailAmount WithDimension(ValueDimension dimension) => In(dimension, _baseUnits);

    public int CompareTo(RailAmount other) => _baseUnits.CompareTo(other._baseUnits);

    /// <summary>The canonical decimal base-unit rendering — the form persisted and transported.</summary>
    public string ToBaseUnitString() => _baseUnits.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => $"{ToBaseUnitString()} {Dimension}";
}
