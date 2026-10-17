// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.DependencyInjection.Extensions;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Analytics;

/// <summary>
/// DI wiring for the D16 value-rate seam. See <c>Services/Analytics/AGENTS.md</c> §wiring.
/// </summary>
/// <remarks>
/// This directory is not covered by the <c>Services/Actions/Handlers</c> assembly scan, so
/// nothing here is registered by convention. Without the one call below the node keeps the
/// honest default — <see cref="UnpricedValueRateSource"/> via the manager's optional
/// constructor parameter — and every operation settles unpriced rather than mispriced.
/// </remarks>
public static class AnalyticsRegistration
{
    /// <summary>
    /// Registers the tenant-declared rate source and the configuration-backed store behind
    /// it. Call it from <c>Program.cs</c>:
    /// <c>builder.Services.AddValueRateSources(builder.Configuration);</c>
    /// </summary>
    /// <remarks>
    /// Both registrations are <c>TryAdd</c>, so a durable
    /// <see cref="ITenantDeclaredRateStore"/> (the reserve exchange registers one) or a
    /// different <see cref="IValueRateSource"/> registered BEFORE this call wins and this
    /// method adds only what is still missing. Registering a second rate source after this
    /// call would leave the last registration serving every resolve, which is why the
    /// override goes first.
    /// </remarks>
    public static IServiceCollection AddValueRateSources(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TenantDeclaredRateOptions>(
            configuration.GetSection(TenantDeclaredRateOptions.SectionName));

        // Singleton: the configured declarations are parsed once and never change without a
        // restart. A store reading declarations registered over the wire replaces this one.
        services.TryAddSingleton<ITenantDeclaredRateStore, ConfiguredTenantDeclaredRateStore>();

        // Scoped to match IBlockchainOperationManager, the only consumer, so a request-scoped
        // store implementation stays resolvable from it.
        services.TryAddScoped<IValueRateSource, TenantDeclaredValueRateSource>();

        return services;
    }
}
