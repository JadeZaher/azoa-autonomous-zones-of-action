// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Analytics;

/// <summary>
/// Stamps a settled operation with the USD value the TENANT declared for the asset it
/// moved (D7/D16). See <c>Services/Analytics/AGENTS.md</c> §declared-rate-source.
/// </summary>
/// <remarks>
/// Every input of the stamp — rate, source, observation instant — comes out of the tenant's
/// own declaration; this class computes no economics and holds no opinion about price. Its
/// entire job is to REFUSE: a dimension that does not name its asset, a chain the
/// declaration is not for, a currency D15 does not pin, a declaration dated after the
/// settlement it would price, and one older than the configured window are each answered
/// with <c>null</c>, which the manager records as "never priced".
/// </remarks>
public sealed class TenantDeclaredValueRateSource : IValueRateSource
{
    /// <summary>D15 pins the stamped currency to USD; a declaration in anything else is declined, never converted.</summary>
    private const string SupportedCurrencyCode = "USD";

    /// <summary>Clock-skew grace before a declaration dated ahead of its own settlement is refused.</summary>
    private static readonly TimeSpan FutureDeclarationTolerance = TimeSpan.FromMinutes(5);

    private readonly ITenantDeclaredRateStore _declarations;
    private readonly IOptions<TenantDeclaredRateOptions> _options;
    private readonly ILogger<TenantDeclaredValueRateSource>? _logger;

    /// <param name="logger">Optional so a test can build this in one line; the container supplies a real one.</param>
    public TenantDeclaredValueRateSource(
        ITenantDeclaredRateStore declarations,
        IOptions<TenantDeclaredRateOptions> options,
        ILogger<TenantDeclaredValueRateSource>? logger = null)
    {
        _declarations = declarations ?? throw new ArgumentNullException(nameof(declarations));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask<ValueRateQuote?> TryQuoteAsync(
        ValueDimension dimension,
        string chainType,
        DateTimeOffset asOf,
        CancellationToken ct = default)
    {
        // A fiat amount is already money. Re-pricing it here would either restate what the
        // fiat rail already knows or invent an FX rate nobody declared.
        if (dimension.IsFiat)
            return null;

        // THE D16 TRAP, closed here: an unnamed dimension means "whatever asset the call
        // itself named", and the chain type alone does not identify it — every ASA on
        // Algorand is chain type "Algorand". Pricing it would stamp N project-token units
        // at the native rate, i.e. N micro-Algos on a sponsor's statement.
        var assetId = dimension.Code.Trim();
        if (assetId.Length == 0)
        {
            _logger?.LogDebug(
                "Declined to price an amount whose dimension names no asset on {ChainType}; "
                + "the operation is recorded unpriced.",
                chainType);
            return null;
        }

        if (string.IsNullOrWhiteSpace(chainType))
            return null;

        var maxAge = _options.Value.MaxDeclarationAge;
        if (maxAge <= TimeSpan.Zero)
            return null;

        var declaration = await _declarations.TryGetLatestAsync(chainType, assetId, ct);
        if (declaration is null)
            return null;

        // Defence in depth against a store that keys loosely: a declaration for a different
        // asset or chain is a different price, never a fallback for this one.
        if (!string.Equals(declaration.ChainType, chainType.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(declaration.AssetId, assetId, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "Declared-rate store returned a rate for {DeclaredChain}/{DeclaredAsset} when asked for "
                + "{RequestedChain}/{RequestedAsset}; refusing it and recording the operation unpriced.",
                declaration.ChainType, declaration.AssetId, chainType, assetId);
            return null;
        }

        if (!string.Equals(declaration.CurrencyCode, SupportedCurrencyCode, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "Declared rate for {ChainType}/{AssetId} is in {CurrencyCode}; only {Supported} is stamped today (D15).",
                chainType, assetId, declaration.CurrencyCode, SupportedCurrencyCode);
            return null;
        }

        if (declaration.DeclaredAt > asOf + FutureDeclarationTolerance)
        {
            _logger?.LogWarning(
                "Declared rate for {ChainType}/{AssetId} is dated {DeclaredAt}, after the settlement it would "
                + "price ({AsOf}); refusing it.",
                chainType, assetId, declaration.DeclaredAt, asOf);
            return null;
        }

        // Stale-presented-as-current is the fabrication §value-stamp forbids by name: past
        // the window the honest answer is no stamp, not the last number the tenant sent.
        if (asOf - declaration.DeclaredAt > maxAge)
        {
            _logger?.LogWarning(
                "Declared rate for {ChainType}/{AssetId} was observed {DeclaredAt}, older than the {MaxAge} "
                + "window; recording the operation unpriced rather than stamping a stale rate.",
                chainType, assetId, declaration.DeclaredAt, maxAge);
            return null;
        }

        return new ValueRateQuote(
            ValueDimension.Fiat(declaration.CurrencyCode),
            declaration.MinorUnitsPerBaseUnit,
            declaration.DeclaredAt,
            DescribeSource(declaration));
    }

    /// <summary>Provenance carried onto the row: the tenant's own source, its version, and the fact that it was declared rather than observed by AZOA.</summary>
    private static string DescribeSource(TenantDeclaredRate declaration) =>
        declaration.Version is null
            ? $"tenant-declared:{declaration.Source}"
            : $"tenant-declared:{declaration.Source}@{declaration.Version}";
}

/// <summary>Configuration for the declared-rate seam — the <c>ValueRates</c> section.</summary>
public sealed class TenantDeclaredRateOptions
{
    public const string SectionName = "ValueRates";

    /// <summary>
    /// How old a declaration may be at the settlement it prices. Non-positive disables the
    /// source entirely (nothing is stamped) — the fail-closed reading, never "no limit".
    /// </summary>
    public TimeSpan MaxDeclarationAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Rates declared in configuration. Empty — the default, and the state of a node whose
    /// consumer has registered nothing — means the node prices nothing at all.
    /// </summary>
    public IList<DeclaredRateEntry> Declared { get; set; } = new List<DeclaredRateEntry>();
}

/// <summary>One <c>ValueRates:Declared</c> entry, as written in configuration.</summary>
/// <remarks>
/// Numbers are carried as strings so configuration binding cannot quietly round a money
/// constant through a floating-point conversion; they are parsed with invariant culture in
/// <see cref="ConfiguredTenantDeclaredRateStore"/>.
/// </remarks>
public sealed class DeclaredRateEntry
{
    /// <summary>Chain the asset lives on, matching the provider's <c>ChainType</c> ("Algorand").</summary>
    public string? ChainType { get; set; }

    /// <summary>Asset the rate prices — ASA id, SPL mint, ERC-20 address. Required: an unnamed asset is not a declaration.</summary>
    public string? AssetId { get; set; }

    /// <summary>ISO-4217 code the rate converts into. USD only today (D15).</summary>
    public string? CurrencyCode { get; set; } = "USD";

    /// <summary>Minor units per ONE BASE UNIT of the asset. Mutually exclusive with <see cref="MajorUnitsPerWholeToken"/>.</summary>
    public string? MinorUnitsPerBaseUnit { get; set; }

    /// <summary>Price of one WHOLE token in the currency's major unit (dollars). Requires <see cref="AssetDecimals"/>.</summary>
    public string? MajorUnitsPerWholeToken { get; set; }

    /// <summary>The asset's decimals, needed to convert <see cref="MajorUnitsPerWholeToken"/> to a per-base-unit rate.</summary>
    public int? AssetDecimals { get; set; }

    /// <summary>Digits in the currency's minor unit — 2 for USD cents.</summary>
    public int CurrencyMinorUnitDigits { get; set; } = 2;

    /// <summary>When the tenant observed the rate, ISO-8601. A value with no offset is read as UTC.</summary>
    public string? DeclaredAt { get; set; }

    /// <summary>The tenant's own name for where the rate came from ("ardanova:treasury-rate").</summary>
    public string? Source { get; set; }

    /// <summary>The tenant's version for the schedule this rate belongs to, when it versions one.</summary>
    public string? Version { get; set; }
}

/// <summary>
/// The declared-rate store a node runs before its consumer registers rates over the wire:
/// the <c>ValueRates:Declared</c> entries, parsed once.
/// </summary>
/// <remarks>
/// A malformed or duplicated entry throws at construction rather than being skipped. A
/// money-rate typo that silently disabled pricing would look exactly like a node whose
/// consumer declared nothing, and the two must not be confusable.
/// </remarks>
public sealed class ConfiguredTenantDeclaredRateStore : ITenantDeclaredRateStore
{
    private readonly IReadOnlyDictionary<string, TenantDeclaredRate> _byChainAndAsset;

    public ConfiguredTenantDeclaredRateStore(IOptions<TenantDeclaredRateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var declarations = new Dictionary<string, TenantDeclaredRate>(StringComparer.Ordinal);
        var entries = options.Value.Declared ?? new List<DeclaredRateEntry>();

        for (var index = 0; index < entries.Count; index++)
        {
            var declaration = Parse(entries[index], index);
            var key = KeyOf(declaration.ChainType, declaration.AssetId);
            if (!declarations.TryAdd(key, declaration))
                throw new InvalidOperationException(
                    $"{TenantDeclaredRateOptions.SectionName}:Declared[{index}] declares a second rate for "
                    + $"{declaration.ChainType}/{declaration.AssetId}; which one prices an operation is "
                    + "ambiguous, so the node refuses to start with both.");
        }

        _byChainAndAsset = declarations;
    }

    /// <inheritdoc/>
    public ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
        string chainType,
        string assetId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chainType) || string.IsNullOrWhiteSpace(assetId))
            return ValueTask.FromResult<TenantDeclaredRate?>(null);

        return ValueTask.FromResult<TenantDeclaredRate?>(
            _byChainAndAsset.TryGetValue(KeyOf(chainType, assetId), out var declaration)
                ? declaration
                : null);
    }

    /// <summary>Chain type folds case (providers spell it "Algorand"); an asset id never does.</summary>
    private static string KeyOf(string chainType, string assetId) =>
        $"{chainType.Trim().ToUpperInvariant()}\u001f{assetId.Trim()}";

    private static TenantDeclaredRate Parse(DeclaredRateEntry entry, int index)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var chainType = Required(entry.ChainType, nameof(DeclaredRateEntry.ChainType), index);
        var assetId = Required(entry.AssetId, nameof(DeclaredRateEntry.AssetId), index);
        var source = Required(entry.Source, nameof(DeclaredRateEntry.Source), index);
        var currencyCode = string.IsNullOrWhiteSpace(entry.CurrencyCode) ? "USD" : entry.CurrencyCode.Trim();
        var declaredAt = ParseDeclaredAt(entry, index);

        var hasPerBaseUnit = !string.IsNullOrWhiteSpace(entry.MinorUnitsPerBaseUnit);
        var hasWholeToken = !string.IsNullOrWhiteSpace(entry.MajorUnitsPerWholeToken);
        if (hasPerBaseUnit == hasWholeToken)
            throw Invalid(
                index,
                "must set exactly one of MinorUnitsPerBaseUnit or MajorUnitsPerWholeToken — "
                + "two spellings of one price is how a 100x lands on a financial record");

        if (hasPerBaseUnit)
        {
            return new TenantDeclaredRate(
                chainType,
                assetId,
                currencyCode,
                ParseDecimal(entry.MinorUnitsPerBaseUnit!, nameof(DeclaredRateEntry.MinorUnitsPerBaseUnit), index),
                declaredAt,
                source,
                entry.Version);
        }

        if (entry.AssetDecimals is not { } assetDecimals)
            throw Invalid(index, "sets MajorUnitsPerWholeToken but no AssetDecimals to convert it with");

        try
        {
            return TenantDeclaredRate.FromWholeUnitPrice(
                chainType,
                assetId,
                currencyCode,
                ParseDecimal(entry.MajorUnitsPerWholeToken!, nameof(DeclaredRateEntry.MajorUnitsPerWholeToken), index),
                assetDecimals,
                entry.CurrencyMinorUnitDigits,
                declaredAt,
                source,
                entry.Version);
        }
        catch (ArgumentException ex)
        {
            throw Invalid(index, ex.Message);
        }
    }

    private static DateTimeOffset ParseDeclaredAt(DeclaredRateEntry entry, int index)
    {
        var raw = Required(entry.DeclaredAt, nameof(DeclaredRateEntry.DeclaredAt), index);
        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var declaredAt))
        {
            throw Invalid(index, $"has an unreadable DeclaredAt '{raw}'; expected ISO-8601");
        }

        return declaredAt;
    }

    private static decimal ParseDecimal(string raw, string field, int index) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Invalid(index, $"has an unreadable {field} '{raw}'");

    private static string Required(string? value, string field, int index) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Invalid(index, $"is missing {field}")
            : value.Trim();

    private static InvalidOperationException Invalid(int index, string problem) =>
        new($"{TenantDeclaredRateOptions.SectionName}:Declared[{index}] {problem}.");
}
