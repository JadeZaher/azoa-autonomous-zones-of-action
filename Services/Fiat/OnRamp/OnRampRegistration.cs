// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OnRamp;

/// <summary>
/// DI wiring for the fiat on-ramp: the price schedule, the lot ledger, the settlement handler,
/// and the price schedule's role as the D16 rate source. Called from <c>Program.cs</c> (W4.1 owns
/// that file for this wave and calls this as a fully-qualified static).
/// </summary>
/// <remarks>
/// Nothing in this directory is registered by convention, so without the one call below the node
/// boots with NO <c>IFiatSettlementHandler</c> — which <c>FiatRailStartup.Validate</c> refuses to
/// do in the live environment, precisely so a card cannot be charged by a node that cannot credit
/// it. A test-environment node would boot and take test payments it never credits, which is why
/// the guards here throw rather than skipping a missing collaborator.
/// </remarks>
public static class OnRampRegistration
{
    /// <summary>
    /// Registers the on-ramp: the price schedule, the lot ledger and the settlement handler.
    /// Call it after the reserve, the allocation seam and the fiat rail; then call
    /// <see cref="AddOnRampValueRateSource"/> after <c>AddValueRateSources</c>. Every ordering
    /// requirement is checked here rather than trusted, so a wiring change fails at boot.
    /// </summary>
    public static IServiceCollection AddFiatOnRamp(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(descriptor => descriptor.ServiceType != typeof(ISurrealExecutor)))
            throw new InvalidOperationException(
                "AddFiatOnRamp requires ISurrealExecutor to be registered first (AddSurrealForge): the "
                + "price schedule and the lot ledger are both durable.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(IAllocationManager)))
            throw new InvalidOperationException(
                "AddFiatOnRamp requires IAllocationManager to be registered first: a cleared payment is "
                + "credited by issuing units from the platform reserve through the allocation seam.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(IPlatformTokenGenesis)))
            throw new InvalidOperationException(
                "AddFiatOnRamp requires IPlatformTokenGenesis to be registered first: the on-ramp issues "
                + "the platform unit, and it reads which asset that is rather than being told.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(IRealValueKycGate)))
            throw new InvalidOperationException(
                "AddFiatOnRamp requires IRealValueKycGate to be registered first: buying units is a value "
                + "action and D46 gates every value action on a current identity approval.");

        // Bound as an instance resolved from configuration (the ReserveOptions/HoldOptions
        // precedent): an absent section must still yield the fail-closed defaults, never null.
        services.AddSingleton(provider =>
            provider.GetRequiredService<IConfiguration>()
                .GetSection(OnRampOptions.SectionName)
                .Get<OnRampOptions>()
            ?? new OnRampOptions());

        services.AddScoped<ITenantPriceScheduleStore, SurrealTenantPriceScheduleStore>();
        services.AddScoped<IOnRampLotLedger, SurrealOnRampLotLedger>();

        // Registered as BOTH so the webhook controller's IEnumerable<IFiatEventHandler> picks it
        // up and the boot guard's IFiatSettlementHandler probe can see it. Two AddScoped calls
        // for one implementation would build two instances per request; the second resolves the
        // first, so there is exactly one handler per delivery.
        services.AddScoped<OnRampSettlementHandler>();
        services.AddScoped<IFiatSettlementHandler>(provider =>
            provider.GetRequiredService<OnRampSettlementHandler>());
        services.AddScoped<IFiatEventHandler>(provider =>
            provider.GetRequiredService<OnRampSettlementHandler>());

        return services;
    }

    /// <summary>
    /// Makes the registered price schedule the source of the D16 value stamp, so the USD figure
    /// on an issuance is the SAME price that converted the money.
    /// </summary>
    /// <remarks>
    /// <para>This is the read side of the pricing control, and it is deliberately not left to
    /// call ordering. <c>AnalyticsRegistration.AddValueRateSources</c> installs its
    /// configuration-backed store with <c>TryAdd</c>, so whichever call runs first wins; a wiring
    /// change in <c>Program.cs</c> could silently leave the schedule deciding quantities while a
    /// second, independently configured rate decided what the operation was worth. This method
    /// therefore REPLACES the registration and composes whatever was there as the inner fallback,
    /// so assets with no registered schedule (a project token priced from configuration) keep
    /// working and the platform unit is priced by the schedule that sold it.</para>
    /// <para>Call it AFTER <c>AddValueRateSources</c>.</para>
    /// </remarks>
    public static IServiceCollection AddOnRampValueRateSource(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(descriptor => descriptor.ServiceType != typeof(ITenantPriceScheduleStore)))
            throw new InvalidOperationException(
                "AddOnRampValueRateSource requires AddFiatOnRamp to have run first: the price schedule it "
                + "reads is registered there.");

        // The store being displaced becomes the inner fallback. Resolving it through a factory
        // captured BEFORE the replacement is what keeps this from being a self-reference.
        var existing = services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(ITenantDeclaredRateStore));
        if (existing is null)
        {
            throw new InvalidOperationException(
                "AddOnRampValueRateSource requires AddValueRateSources to have run first: it composes the "
                + "configured declared-rate store as the fallback for assets with no registered price, "
                + "and there is nothing to compose.");
        }

        var innerLifetime = existing.Lifetime;
        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(ConfiguredTenantDeclaredRateStoreMarker),
            provider => new ConfiguredTenantDeclaredRateStoreMarker(Materialise(existing, provider)),
            innerLifetime));

        services.Add(new ServiceDescriptor(
            typeof(ITenantDeclaredRateStore),
            provider => new PriceScheduleDeclaredRateStore(
                provider.GetRequiredService<ITenantPriceScheduleStore>(),
                provider.GetRequiredService<ConfiguredTenantDeclaredRateStoreMarker>().Inner,
                provider.GetService<ILogger<PriceScheduleDeclaredRateStore>>()),
            ServiceLifetime.Scoped));

        // IValueRateSource itself is left alone: AddValueRateSources registers it scoped, which
        // can resolve the scoped store above. Replacing it here would silently discard a rate
        // source an operator had deliberately registered ahead of it.
        return services;
    }

    /// <summary>Builds the displaced declared-rate store from the descriptor it was registered with.</summary>
    private static ITenantDeclaredRateStore Materialise(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        // Reading Implementation* on a KEYED descriptor throws. Nothing registers the declared-rate
        // store by key today; if something ever does, say so plainly instead of throwing from a
        // property getter three frames down.
        if (descriptor.IsKeyedService)
            throw new InvalidOperationException(
                "The registered ITenantDeclaredRateStore is a keyed service, which the on-ramp cannot "
                + "compose as a fallback. Register it unkeyed, or give the on-ramp its own fallback.");

        if (descriptor.ImplementationInstance is ITenantDeclaredRateStore instance)
            return instance;
        if (descriptor.ImplementationFactory is { } factory)
            return (ITenantDeclaredRateStore)factory(provider);
        if (descriptor.ImplementationType is { } type)
            return (ITenantDeclaredRateStore)ActivatorUtilities.CreateInstance(provider, type);

        throw new InvalidOperationException(
            "The registered ITenantDeclaredRateStore could not be constructed, so the on-ramp cannot "
            + "compose it as a fallback. Refusing rather than dropping it: dropping it would silently "
            + "stop pricing every asset that has no registered schedule.");
    }

    /// <summary>
    /// Holds the declared-rate store the on-ramp displaced, so it can be composed as a fallback
    /// without the composed instance resolving itself.
    /// </summary>
    public sealed class ConfiguredTenantDeclaredRateStoreMarker
    {
        public ConfiguredTenantDeclaredRateStoreMarker(ITenantDeclaredRateStore inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>The store that was registered before the on-ramp took over.</summary>
        public ITenantDeclaredRateStore Inner { get; }
    }
}
