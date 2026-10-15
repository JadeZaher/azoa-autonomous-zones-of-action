// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// The write half of the declared-rate seam. Exchanges record the rate they were priced at,
/// so the figure that decided the quantity is the same figure the value stamp reads back.
/// </summary>
public interface IExchangeDeclaredRateWriter
{
    /// <summary>
    /// Persists <paramref name="rate"/> as the latest declaration for its chain and asset.
    /// An OLDER declaration never displaces a newer one, so an out-of-order retry cannot rewind
    /// the rate a later exchange already settled at.
    /// </summary>
    Task<AZOAResult<TenantDeclaredRate>> RecordAsync(
        TenantDeclaredRate rate, CancellationToken ct = default);
}

/// <summary>
/// The durable declared-rate store the reserve exchange installs (the one
/// <c>IValueRateSource</c>'s contract says "lands with the reserve exchange"). Declarations
/// live as holons, so the store needs no schema of its own, and anything the node already knew
/// how to price stays priceable through the composed fallback.
/// </summary>
/// <remarks>
/// <para>AZOA still computes nothing here (D7). Every field of a declaration comes from the
/// consumer that declared it — rate, currency, source, version, observation instant — and is
/// relayed back out unchanged.</para>
/// <para>The fallback is composed rather than replaced so the on-ramp's price schedule keeps
/// pricing the platform unit it sold, while project assets are priced by the rate the exchange
/// that moved them was booked at.</para>
/// </remarks>
public sealed class HolonTenantDeclaredRateStore : ITenantDeclaredRateStore, IExchangeDeclaredRateWriter
{
    /// <summary>Holon asset type every declaration row carries. Never reused by another lane.</summary>
    public const string DeclaredRateAssetType = "AzoaDeclaredRate";

    /// <summary>Provider name stamped on a declaration row.</summary>
    public const string ProviderName = "azoa.exchange";

    private static class MetadataKeys
    {
        public const string CurrencyCode = "currencyCode";
        public const string MinorUnitsPerBaseUnit = "minorUnitsPerBaseUnit";
        public const string DeclaredAt = "declaredAt";
        public const string Source = "source";
        public const string Version = "version";
    }

    private readonly IHolonStore _holons;
    private readonly ITenantDeclaredRateStore? _fallback;
    private readonly ILogger<HolonTenantDeclaredRateStore>? _logger;

    /// <param name="fallback">
    /// The store this one displaced. Consulted only when no declaration exists here, so an
    /// asset the exchange has never priced keeps whatever pricing the node already had.
    /// </param>
    public HolonTenantDeclaredRateStore(
        IHolonStore holons,
        ITenantDeclaredRateStore? fallback = null,
        ILogger<HolonTenantDeclaredRateStore>? logger = null)
    {
        _holons = holons ?? throw new ArgumentNullException(nameof(holons));
        _fallback = fallback;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
        string chainType, string assetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chainType) || string.IsNullOrWhiteSpace(assetId))
            return null;

        var stored = await ReadAsync(chainType.Trim(), assetId.Trim(), ct);
        if (stored is not null)
            return stored;

        return _fallback is null ? null : await _fallback.TryGetLatestAsync(chainType, assetId, ct);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<TenantDeclaredRate>> RecordAsync(
        TenantDeclaredRate rate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rate);

        var existing = await ReadAsync(rate.ChainType, rate.AssetId, ct);
        if (existing is not null && existing.DeclaredAt > rate.DeclaredAt)
        {
            // A late-arriving older declaration is not new information about the price; writing
            // it would silently re-price everything stamped after it.
            _logger?.LogDebug(
                "Kept the existing declaration for {ChainType}/{AssetId} observed at {Existing}; the "
                + "incoming one was observed earlier ({Incoming}).",
                rate.ChainType, rate.AssetId, existing.DeclaredAt, rate.DeclaredAt);
            return AZOAResult<TenantDeclaredRate>.Success(existing, "A newer declaration is already recorded.");
        }

        var row = new Holon
        {
            Id = RecordIdFor(rate.ChainType, rate.AssetId),
            Name = NameFor(rate.ChainType, rate.AssetId),
            Description = "Consumer-declared rate for one asset on one rail.",
            ProviderName = ProviderName,
            ChainId = rate.ChainType,
            AssetType = DeclaredRateAssetType,
            TokenId = rate.AssetId,
            IsActive = true,
            ModifiedDate = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MetadataKeys.CurrencyCode] = rate.CurrencyCode,
                [MetadataKeys.MinorUnitsPerBaseUnit] =
                    rate.MinorUnitsPerBaseUnit.ToString(CultureInfo.InvariantCulture),
                [MetadataKeys.DeclaredAt] = rate.DeclaredAt.ToString("O", CultureInfo.InvariantCulture),
                [MetadataKeys.Source] = rate.Source,
                [MetadataKeys.Version] = rate.Version ?? string.Empty,
            },
        };

        var written = await _holons.UpsertAsync(row, ct);
        return written.IsError
            ? AZOAResult<TenantDeclaredRate>.Failure(written.Message)
            : AZOAResult<TenantDeclaredRate>.Success(rate, "Declared rate recorded.");
    }

    /// <summary>The stored declaration, or null when there is none or it cannot be read faithfully.</summary>
    private async Task<TenantDeclaredRate?> ReadAsync(string chainType, string assetId, CancellationToken ct)
    {
        AZOAResult<IHolon> row;
        try
        {
            row = await _holons.GetByIdAsync(RecordIdFor(chainType, assetId), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A rate source that throws is treated exactly like one that declines: the operation
            // is recorded unpriced rather than priced from a guess.
            _logger?.LogWarning(
                exception,
                "Could not read the declared rate for {ChainType}/{AssetId}; treating it as unpriced.",
                chainType, assetId);
            return null;
        }

        if (row.IsError || row.Result is null || !row.Result.IsActive)
            return null;

        var metadata = row.Result.Metadata;
        if (!metadata.TryGetValue(MetadataKeys.MinorUnitsPerBaseUnit, out var rateText)
            || !decimal.TryParse(
                rateText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rateValue)
            || !metadata.TryGetValue(MetadataKeys.DeclaredAt, out var declaredAtText)
            || !DateTimeOffset.TryParse(
                declaredAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var declaredAt)
            || !metadata.TryGetValue(MetadataKeys.Source, out var source)
            || string.IsNullOrWhiteSpace(source))
        {
            _logger?.LogWarning(
                "The declared-rate record for {ChainType}/{AssetId} is incomplete; treating it as unpriced "
                + "rather than reconstructing a partial price.",
                chainType, assetId);
            return null;
        }

        // Defence in depth against a row that drifted from its own key: a rate for another asset
        // is a different price, never a fallback for this one.
        var storedChain = row.Result.ChainId ?? string.Empty;
        var storedAsset = row.Result.TokenId ?? string.Empty;
        if (!string.Equals(storedChain, chainType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(storedAsset, assetId, StringComparison.Ordinal))
        {
            return null;
        }

        metadata.TryGetValue(MetadataKeys.CurrencyCode, out var currencyCode);
        metadata.TryGetValue(MetadataKeys.Version, out var version);

        try
        {
            return new TenantDeclaredRate(
                storedChain,
                storedAsset,
                string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode,
                rateValue,
                declaredAt,
                source,
                string.IsNullOrWhiteSpace(version) ? null : version);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Human-readable row name; the id is what addresses it.</summary>
    private static string NameFor(string chainType, string assetId) =>
        $"declared-rate:{chainType.ToUpperInvariant()}:{assetId}";

    /// <summary>
    /// One deterministic id per (chain, asset), so a read addresses the exact row a write
    /// produced. A name search would substring-match and could return another asset's price.
    /// </summary>
    private static Guid RecordIdFor(string chainType, string assetId)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"azoa:declared-rate:v1:{chainType.Trim().ToUpperInvariant()}:{assetId.Trim()}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Installs this store as the node's <see cref="ITenantDeclaredRateStore"/>, composing
    /// whatever was registered before it as the fallback.
    /// </summary>
    /// <remarks>
    /// Composition, not replacement, is the point: the on-ramp's price schedule must keep
    /// pricing the platform unit it sold, and dropping it would silently stop pricing every
    /// asset the exchange has not touched. Modelled on
    /// <c>OnRampRegistration.AddOnRampValueRateSource</c>, which does the same thing for the
    /// same reason.
    /// </remarks>
    public static IServiceCollection Install(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var existing = services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(ITenantDeclaredRateStore));
        if (existing is null)
        {
            throw new InvalidOperationException(
                "The reserve exchange's declared-rate store requires AddValueRateSources to have run "
                + "first: it composes the already-registered store as the fallback, and there is "
                + "nothing to compose.");
        }

        if (existing.IsKeyedService)
        {
            throw new InvalidOperationException(
                "The registered ITenantDeclaredRateStore is a keyed service, which the reserve exchange "
                + "cannot compose as a fallback. Register it unkeyed.");
        }

        var innerLifetime = existing.Lifetime;
        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(DisplacedDeclaredRateStore),
            provider => new DisplacedDeclaredRateStore(Materialise(existing, provider)),
            innerLifetime));

        // Scoped: the holon store it writes through is scoped, and IValueRateSource is scoped too.
        services.Add(new ServiceDescriptor(
            typeof(HolonTenantDeclaredRateStore),
            provider => new HolonTenantDeclaredRateStore(
                provider.GetRequiredService<IHolonStore>(),
                provider.GetRequiredService<DisplacedDeclaredRateStore>().Inner,
                provider.GetService<ILogger<HolonTenantDeclaredRateStore>>()),
            ServiceLifetime.Scoped));

        services.Add(new ServiceDescriptor(
            typeof(ITenantDeclaredRateStore),
            provider => provider.GetRequiredService<HolonTenantDeclaredRateStore>(),
            ServiceLifetime.Scoped));

        services.Add(new ServiceDescriptor(
            typeof(IExchangeDeclaredRateWriter),
            provider => provider.GetRequiredService<HolonTenantDeclaredRateStore>(),
            ServiceLifetime.Scoped));

        return services;
    }

    /// <summary>Builds the displaced store from the descriptor it was registered with.</summary>
    private static ITenantDeclaredRateStore Materialise(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        if (descriptor.ImplementationInstance is ITenantDeclaredRateStore instance)
            return instance;
        if (descriptor.ImplementationFactory is { } factory)
            return (ITenantDeclaredRateStore)factory(provider);
        if (descriptor.ImplementationType is { } type)
            return (ITenantDeclaredRateStore)ActivatorUtilities.CreateInstance(provider, type);

        throw new InvalidOperationException(
            "The registered ITenantDeclaredRateStore could not be constructed, so the reserve exchange "
            + "cannot compose it as a fallback. Refusing rather than dropping it: dropping it would "
            + "silently stop pricing every asset the exchange has never seen.");
    }

    /// <summary>
    /// Holds the declared-rate store the exchange displaced, so the composed instance does not
    /// resolve itself.
    /// </summary>
    public sealed class DisplacedDeclaredRateStore
    {
        public DisplacedDeclaredRateStore(ITenantDeclaredRateStore inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>The store that was registered before the exchange took over.</summary>
        public ITenantDeclaredRateStore Inner { get; }
    }
}
