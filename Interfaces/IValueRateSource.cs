// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// The seam a rate source plugs into so a settled operation can be stamped with its
/// fiat value at transaction time (decision D16).
/// </summary>
/// <remarks>
/// Two implementations ship: <see cref="UnpricedValueRateSource"/>, which never quotes and
/// is what a node without the registration falls back to, and
/// <c>Services/Analytics/TenantDeclaredValueRateSource</c>, which relays a rate the tenant
/// DECLARED for one named chain asset and refuses everything else. AZOA never computes an
/// exchange rate of its own (D7): it records the one its consumer decided, with that
/// consumer's own source and timestamp, or it records nothing.
/// See <c>Services/Analytics/AGENTS.md</c> §declared-rate-source and
/// <c>Persistence/SurrealDb/Models/AGENTS.md</c> §value-stamp.
/// </remarks>
public interface IValueRateSource
{
    /// <summary>
    /// The rate for <paramref name="dimension"/> at (or nearest before)
    /// <paramref name="asOf"/>, or <c>null</c> when this source cannot price it.
    /// </summary>
    /// <remarks>
    /// Returning <c>null</c> is a first-class answer and MUST be used whenever the
    /// source is unsure: the operation is then written with no stamp at all, which the
    /// analytics surface reads as "never priced" and which stays distinguishable from a
    /// genuine quote of zero. An implementation must never substitute a default, a last
    /// known value presented as current, or zero.
    /// <para>An implementation must also decline a <paramref name="dimension"/> that does
    /// not NAME its asset (<see cref="ValueDimension.NativeChainAsset"/>, whose
    /// <see cref="ValueDimension.Code"/> is empty). The chain type alone does not identify
    /// an asset — every ASA on Algorand shares the chain type "Algorand" — so pricing an
    /// unnamed dimension is how N project-token units become N micro-Algos on a sponsor's
    /// statement.</para>
    /// </remarks>
    /// <param name="dimension">The unit being priced — the chain asset the amount counts.</param>
    /// <param name="chainType">The chain the operation settles on; two chains' "same" asset are not interchangeable.</param>
    /// <param name="asOf">The settlement instant the rate should describe.</param>
    ValueTask<ValueRateQuote?> TryQuoteAsync(
        ValueDimension dimension,
        string chainType,
        DateTimeOffset asOf,
        CancellationToken ct = default);
}

/// <summary>
/// The fallback rate source: it never quotes. Present so the stamping path has a real
/// collaborator to exercise and so "no rate source is wired" is an explicit, testable state
/// rather than a null check scattered through the manager.
/// </summary>
public sealed class UnpricedValueRateSource : IValueRateSource
{
    public static UnpricedValueRateSource Instance { get; } = new();

    public ValueTask<ValueRateQuote?> TryQuoteAsync(
        ValueDimension dimension,
        string chainType,
        DateTimeOffset asOf,
        CancellationToken ct = default)
        => ValueTask.FromResult<ValueRateQuote?>(null);
}

/// <summary>
/// One rate a tenant DECLARED for one named chain asset, with the provenance and the
/// instant the tenant observed it (D7/D16). AZOA stores and relays it; it never derives it.
/// </summary>
/// <remarks>
/// <see cref="MinorUnitsPerBaseUnit"/> is in the stamp's own unit convention — minor units
/// of <see cref="CurrencyCode"/> per ONE BASE UNIT of the asset, not per whole token. At
/// ARDA's 2 decimals, "1 ARDA = $0.01" is <c>0.01</c> minor units per base unit, not
/// <c>1</c>; getting that wrong is a silent 100x on a financial record, which is why
/// <see cref="FromWholeUnitPrice"/> exists and refuses a price it cannot convert exactly.
/// <para>Every field is required because the stamp persists all of them: a figure whose
/// rate, source or observation time is missing cannot be audited or re-derived.</para>
/// </remarks>
public sealed record TenantDeclaredRate
{
    public TenantDeclaredRate(
        string chainType,
        string assetId,
        string currencyCode,
        decimal minorUnitsPerBaseUnit,
        DateTimeOffset declaredAt,
        string source,
        string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainType);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (minorUnitsPerBaseUnit < 0m)
            throw new ArgumentOutOfRangeException(
                nameof(minorUnitsPerBaseUnit), minorUnitsPerBaseUnit,
                "A declared rate cannot be negative.");

        ChainType = chainType.Trim();
        AssetId = assetId.Trim();
        CurrencyCode = currencyCode.Trim().ToUpperInvariant();
        MinorUnitsPerBaseUnit = minorUnitsPerBaseUnit;
        DeclaredAt = declaredAt;
        Source = source.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    /// <summary>The chain the priced asset lives on. Two chains' "same" asset are not interchangeable.</summary>
    public string ChainType { get; }

    /// <summary>The asset this rate prices (ASA id, SPL mint, ERC-20 address). Never blank — an unnamed asset is not a declaration.</summary>
    public string AssetId { get; }

    /// <summary>ISO-4217 code the rate converts into. USD only today (D15).</summary>
    public string CurrencyCode { get; }

    /// <summary>Minor units of <see cref="CurrencyCode"/> per ONE base unit of <see cref="AssetId"/>.</summary>
    public decimal MinorUnitsPerBaseUnit { get; }

    /// <summary>When the tenant observed this rate — not when AZOA read or applied it.</summary>
    public DateTimeOffset DeclaredAt { get; }

    /// <summary>The tenant's own identifier for where the rate came from. Persisted verbatim inside the stamp's source.</summary>
    public string Source { get; }

    /// <summary>The tenant's version for the rate schedule, when it versions one. Persisted alongside the source.</summary>
    public string? Version { get; }

    /// <summary>
    /// A declaration expressed the way a consumer states it — a price in the currency's
    /// MAJOR unit (dollars) for one WHOLE token — converted to the per-base-unit
    /// minor-unit rate the stamp needs.
    /// </summary>
    /// <remarks>
    /// Throws when the conversion cannot be represented exactly rather than rounding: a
    /// rounded money constant is a fabricated one, and a price the node cannot hold exactly
    /// is a price the tenant must restate.
    /// </remarks>
    public static TenantDeclaredRate FromWholeUnitPrice(
        string chainType,
        string assetId,
        string currencyCode,
        decimal majorUnitsPerWholeToken,
        int assetDecimals,
        int currencyMinorUnitDigits,
        DateTimeOffset declaredAt,
        string source,
        string? version = null)
    {
        if (assetDecimals is < 0 or > 18)
            throw new ArgumentOutOfRangeException(
                nameof(assetDecimals), assetDecimals, "Asset decimals must be between 0 and 18.");
        if (currencyMinorUnitDigits is < 0 or > 6)
            throw new ArgumentOutOfRangeException(
                nameof(currencyMinorUnitDigits), currencyMinorUnitDigits,
                "Currency minor-unit digits must be between 0 and 6.");
        if (majorUnitsPerWholeToken < 0m)
            throw new ArgumentOutOfRangeException(
                nameof(majorUnitsPerWholeToken), majorUnitsPerWholeToken,
                "A declared price cannot be negative.");

        var minorUnitsPerWholeToken = majorUnitsPerWholeToken * PowerOfTen(currencyMinorUnitDigits);
        var baseUnitsPerWholeToken = PowerOfTen(assetDecimals);
        var rate = minorUnitsPerWholeToken / baseUnitsPerWholeToken;

        // Refuse silently-rounded money: the round trip must land back on the declared price.
        if (rate * baseUnitsPerWholeToken != minorUnitsPerWholeToken)
            throw new ArgumentException(
                $"{majorUnitsPerWholeToken} per whole token at {assetDecimals} decimals cannot be "
                + "represented exactly as a per-base-unit rate; restate the declared price.",
                nameof(majorUnitsPerWholeToken));

        return new TenantDeclaredRate(
            chainType, assetId, currencyCode, rate, declaredAt, source, version);
    }

    private static decimal PowerOfTen(int exponent)
    {
        var value = 1m;
        for (var i = 0; i < exponent; i++)
            value *= 10m;
        return value;
    }
}

/// <summary>
/// Read seam over the rates a tenant has declared. The durable implementation lands with
/// the reserve exchange (<c>Services/Reserve/TenantDeclaredRateStore</c>); the
/// configuration-backed default ships in <c>Services/Analytics</c>.
/// </summary>
public interface ITenantDeclaredRateStore
{
    /// <summary>
    /// The most recent declaration for <paramref name="assetId"/> on
    /// <paramref name="chainType"/>, or <c>null</c> when the tenant has declared none.
    /// An implementation MUST match the asset exactly — a near-miss is a different asset,
    /// not a fallback.
    /// </summary>
    ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
        string chainType,
        string assetId,
        CancellationToken ct = default);
}
