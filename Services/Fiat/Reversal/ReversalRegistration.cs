// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>
/// DI wiring for the reversal pipeline: the ledger, the server-side fact reads, the compensation
/// ladder, the webhook handler and the durable saga. Called from <c>Program.cs</c> (W4.1 owns
/// that file for this wave and calls this as a fully-qualified static).
/// </summary>
/// <remarks>
/// <para>Nothing in this directory is registered by convention, so without the calls below the
/// node accepts <c>charge.refunded</c> and dispute deliveries and does NOTHING with them: the
/// webhook controller's <c>IEnumerable&lt;IFiatEventHandler&gt;</c> is empty of this handler, so
/// every reversal is answered 202 Ignored and disappears. That is exactly the sixth instance of
/// this codebase's recurring defect — a control that exists in prose and not in the container —
/// so every collaborator this needs is CHECKED here rather than assumed, and a missing one
/// throws at boot.</para>
/// <para>See <c>Services/Fiat/Reversal/AGENTS.md</c> §wiring for the two calls and their order.</para>
/// </remarks>
public static class ReversalRegistration
{
    /// <summary>
    /// Registers the reversal pipeline. Call it AFTER <c>AddFiatOnRamp</c>, the reserve, the hold
    /// command store, the saga layer and the Stripe named HTTP client.
    /// </summary>
    public static IServiceCollection AddFiatReversalPipeline(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        const string Caller = nameof(AddFiatReversalPipeline);

        Require(services, Caller, typeof(ISurrealExecutor),
            "the reversal ledger is durable (AddSurrealForge)");
        Require(services, Caller, typeof(IOnRampLotLedger),
            "a reversal unwinds an on-ramp issuance and reads its lot (AddFiatOnRamp)");
        Require(services, Caller, typeof(IPlatformReserve),
            "recovered units go back to the reserve and its partition counters");
        Require(services, Caller, typeof(IExchangeHoldSourceStore),
            "rung 2 attributes a payment to the person's OWN settled exchange purchases and recovers "
            + "exactly the held share it funded (AddPlatformHold)");
        Require(services, Caller, typeof(IIdempotencyStore),
            "every recovery move is claimed before it is attempted, so a retry replays instead of "
            + "taking units twice");
        Require(services, Caller, typeof(ISagaCoordinator),
            "the compensation ladder runs as a durable saga, not inside the webhook request");
        Require(services, Caller, typeof(IBlockchainProviderFactory),
            "taking units back out of an account needs the chain's clawback capability");
        Require(services, Caller, typeof(IWalletStore),
            "the account units are taken back from is resolved from its own wallet, never guessed");
        Require(services, Caller, typeof(IHttpClientFactory),
            "every reversal figure is re-read from the provider over the named Stripe client");

        // Bound as an instance resolved from configuration (the OnRampOptions precedent): an
        // absent section must still yield the fail-closed defaults, never null.
        services.AddSingleton(provider =>
            provider.GetRequiredService<IConfiguration>()
                .GetSection(ReversalOptions.SectionName)
                .Get<ReversalOptions>()
            ?? new ReversalOptions());

        services.AddScoped<IFiatReversalLedger, SurrealFiatReversalLedger>();
        services.AddScoped<IOnRampLotLookup, SurrealOnRampLotLookup>();
        services.AddSingleton<IReversalFactSource, StripeReversalFactSource>();

        // Rung 2's attribution: the per-lot link from an on-ramp lot to what its units bought.
        // The guard is the same seam the fail-closed stub used to occupy -- a host that registers
        // its OWN provenance source before this call still keeps it -- but the default is now the
        // real link rather than a refusal, which is the whole of OI-59.
        //
        // Scoped, not singleton: it reads through the scoped lot lookup, operation store and hold
        // ledger, and a singleton capturing those would hold one request's Surreal session open
        // for the life of the process.
        //
        // UnavailableReversedPurchaseProvenance is no longer wired anywhere. It stays as the
        // documented fail-closed answer for a node that deliberately refuses to attribute -- such
        // a node registers it itself, ahead of this call.
        if (services.All(descriptor => descriptor.ServiceType != typeof(IReversedPurchaseProvenance)))
            services.AddScoped<IReversedPurchaseProvenance, ExchangePurchaseProvenance>();

        services.AddScoped<IReversalCompensation, ReversalCompensationService>();

        // Registered as IFiatEventHandler ONLY. It credits nothing, so it must never satisfy the
        // live-boot guard's IFiatSettlementHandler probe -- that marker is what proves a node can
        // credit a card before it is allowed to charge one.
        services.AddScoped<IFiatEventHandler, FiatReversalEventHandler>();

        // The durable saga: one definition plus its three step handlers. Without the handlers the
        // processor claims a step and throws resolving it, so they are registered together here
        // and never separately.
        services.AddSingleton<ISagaDefinition>(provider =>
            FiatCompensationSagaDefinition.Build(
                provider.GetRequiredService<ReversalOptions>().EffectiveMaxRecoveryAttempts));

        services.AddScoped<IStepHandler<FiatCompensationPayload>, FiatReversalRecoveryStepHandler>();
        services.AddScoped<IStepHandler<FiatSettlementPayload>, FiatReversalSettlementStepHandler>();
        services.AddScoped<IStepHandler<FiatEscalationPayload>, FiatReversalEscalationStepHandler>();

        return services;
    }

    /// <summary>
    /// Makes the account risk flag a CONTROL: wraps the registered D46 value gate so an account
    /// with an unresolved reversal cannot buy more units or move value out.
    /// </summary>
    /// <remarks>
    /// <para>Call it AFTER the <see cref="IRealValueKycGate"/> registration and after
    /// <see cref="AddFiatReversalPipeline"/>. It REPLACES the registration and composes what was
    /// there as the inner gate, rather than relying on <c>TryAdd</c> ordering — the on-ramp's
    /// value-rate source is displaced the same way and for the same reason: a wiring change in
    /// <c>Program.cs</c> must not be able to quietly disconnect a control.</para>
    /// <para>Without this call the flag is written and nothing reads it, which is the exact shape
    /// of the defect this pipeline exists to avoid. It throws rather than skipping.</para>
    /// </remarks>
    public static IServiceCollection AddReversalRiskValueGate(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Require(services, nameof(AddReversalRiskValueGate), typeof(IFiatReversalLedger),
            "AddFiatReversalPipeline registers the ledger this gate reads");

        var existing = services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IRealValueKycGate));
        if (existing is null)
        {
            throw new InvalidOperationException(
                "AddReversalRiskValueGate requires IRealValueKycGate to be registered first: it wraps "
                + "the D46 value gate, and there is nothing to wrap. Move this call after the gate's "
                + "own registration.");
        }

        // Already applied. The marker is what proves it, NOT the displaced descriptor
        // ImplementationType: this method re-registers IRealValueKycGate through a FACTORY, so on
        // a second call ImplementationType is null and a type comparison never matches. Wrapping
        // twice would read the reversal ledger twice on every value action.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(InnerRealValueKycGateMarker))
            || existing.ImplementationType == typeof(RiskHoldValueGate))
        {
            return services;
        }

        var innerLifetime = existing.Lifetime;
        services.Remove(existing);

        // Resolving the displaced gate through a factory captured BEFORE the replacement is what
        // keeps the wrapper from resolving itself.
        services.Add(new ServiceDescriptor(
            typeof(InnerRealValueKycGateMarker),
            provider => new InnerRealValueKycGateMarker(Materialise(existing, provider)),
            innerLifetime));

        services.Add(new ServiceDescriptor(
            typeof(IRealValueKycGate),
            provider => new RiskHoldValueGate(
                provider.GetRequiredService<InnerRealValueKycGateMarker>().Inner,
                provider.GetRequiredService<IFiatReversalLedger>(),
                provider.GetRequiredService<ILogger<RiskHoldValueGate>>()),
            ServiceLifetime.Scoped));

        return services;
    }

    /// <summary>Throws with the REASON when a collaborator this pipeline needs is not registered.</summary>
    private static void Require(
        IServiceCollection services, string caller, Type serviceType, string because)
    {
        if (services.All(descriptor => descriptor.ServiceType != serviceType))
            throw new InvalidOperationException(
                $"{caller} requires {serviceType.Name} to be registered first: {because}.");
    }

    /// <summary>Builds the displaced value gate from the descriptor it was registered with.</summary>
    private static IRealValueKycGate Materialise(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        // Reading Implementation* on a KEYED descriptor throws. Nothing registers the value gate
        // by key today; if something ever does, say so plainly instead of throwing from a property
        // getter three frames down.
        if (descriptor.IsKeyedService)
            throw new InvalidOperationException(
                "The registered IRealValueKycGate is a keyed service, which the reversal pipeline "
                + "cannot wrap. Register it unkeyed, or give the wrapper its own inner gate.");

        if (descriptor.ImplementationInstance is IRealValueKycGate instance)
            return instance;
        if (descriptor.ImplementationFactory is { } factory)
            return (IRealValueKycGate)factory(provider);
        if (descriptor.ImplementationType is { } type)
            return (IRealValueKycGate)ActivatorUtilities.CreateInstance(provider, type);

        throw new InvalidOperationException(
            "The registered IRealValueKycGate could not be constructed, so the reversal pipeline "
            + "cannot wrap it. Refusing rather than dropping it: dropping it would silently remove "
            + "the identity check from every value action.");
    }

    /// <summary>
    /// Holds the value gate the reversal pipeline displaced, so it can be composed without the
    /// wrapper resolving itself.
    /// </summary>
    public sealed class InnerRealValueKycGateMarker
    {
        public InnerRealValueKycGateMarker(IRealValueKycGate inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>The gate that was registered before the reversal pipeline wrapped it.</summary>
        public IRealValueKycGate Inner { get; }
    }
}
