// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>
/// DI wiring for the fiat off-ramp: the payout ledger, the Connect gateway, the payout account,
/// the cash-out saga and the <c>transfer.reversed</c> handler. Called from <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>Nothing in this directory is registered by convention. Without the one call below the
/// node compiles, ships two controllers MVC will happily route, and answers every one of them
/// with a container error — which is worse than not shipping them, because a cash-out page that
/// 500s looks like an outage rather than an unwired feature. Every collaborator is therefore
/// CHECKED here rather than assumed, and a missing one throws at boot.</para>
/// <para>See <c>Services/Fiat/OffRamp/AGENTS.md</c> §wiring for the call and its position.</para>
/// </remarks>
public static class OffRampRegistration
{
    /// <summary>
    /// Registers the cash-out path. Call it AFTER <c>AddFiatOnRamp</c>, <c>AddFiatReversalPipeline</c>,
    /// the reserve, the saga layer and the named Stripe HTTP client.
    /// </summary>
    public static IServiceCollection AddFiatOffRamp(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        const string Caller = nameof(AddFiatOffRamp);

        Require(services, Caller, typeof(ISurrealExecutor),
            "the payout ledger is durable (AddSurrealForge)");
        Require(services, Caller, typeof(IOnRampLotLedger),
            "the source-aged rule reads the on-ramp lots a cash-out draws down (AddFiatOnRamp)");
        Require(services, Caller, typeof(ITenantPriceScheduleStore),
            "units are converted back into money at the SAME registered price the on-ramp sells at "
            + "(AddFiatOnRamp)");
        Require(services, Caller, typeof(IFiatReversalLedger),
            "a reversed cash-out is recorded as a first-class ledger fact (AddFiatReversalPipeline)");
        Require(services, Caller, typeof(IPlatformTokenGenesis),
            "the off-ramp cashes out the platform unit, and it reads which asset that is rather "
            + "than being told");
        Require(services, Caller, typeof(IPlatformReserve),
            "cashed-out units go back to the reserve and its partition counters");
        Require(services, Caller, typeof(IIdempotencyStore),
            "every leg is claimed before it is attempted, so a retry replays instead of moving "
            + "value twice");
        Require(services, Caller, typeof(ISagaCoordinator),
            "the cash-out runs as a durable saga; the gap between taking units and sending money "
            + "must survive a restart");
        Require(services, Caller, typeof(IBlockchainProviderFactory),
            "collecting units and giving them back are chain moves");
        Require(services, Caller, typeof(IWalletStore),
            "the account units are taken from is resolved from its own wallet, never guessed");
        Require(services, Caller, typeof(IHostedSessionStore),
            "payout-account onboarding is a hosted redirect flow and claims a durable admission");
        Require(services, Caller, typeof(IRealValueKycGate),
            "cashing out is a value action: D46 gates it, and the reversal risk pause wraps that "
            + "same gate (AddReversalRiskValueGate)");
        Require(services, Caller, typeof(IReversalFactSource),
            "tenant top-ups are credited net of fees and reversed from the provider's own charge and dispute "
            + "facts (AddFiatReversalPipeline)");
        Require(services, Caller, typeof(IHttpClientFactory),
            "every payout figure is read and written over the named Stripe client");

        // Bound as an instance resolved from configuration (the OnRampOptions precedent): an
        // absent section must still yield the fail-closed defaults, never null.
        services.AddSingleton(provider =>
            provider.GetRequiredService<IConfiguration>()
                .GetSection(OffRampOptions.SectionName)
                .Get<OffRampOptions>()
            ?? new OffRampOptions());

        services.AddSingleton<IStripeConnectGateway, StripeConnectGateway>();
        services.AddScoped<IFiatPayoutLedger, SurrealFiatPayoutLedger>();
        services.AddScoped<IPayoutAccountService, PayoutAccountService>();
        services.AddScoped<IOffRamp, OffRampService>();

        // D-8 tenant-funded payouts (Services/Fiat/OffRamp/AGENTS.md §tenant-funded): the float ledger,
        // its domain service, top-up credit/reversal handlers (bookkeeping, never settlement handlers),
        // the operator/tenant views, and the sweep that repairs stranded reservations.
        services.AddScoped<ITenantPayoutLedger, SurrealTenantPayoutLedger>();
        services.AddScoped<ITenantPayoutFunding, TenantPayoutFunding>();
        services.AddScoped<IFiatEventHandler, TenantPayoutTopUpHandler>();
        services.AddScoped<IFiatEventHandler, TenantPayoutTopUpReversalHandler>();
        services.AddScoped<TenantPayoutSweep>();
        services.AddHostedService<TenantPayoutSweepWorker>();
        services.AddScoped<IFiatEventHandler, BankPayoutEventHandler>();
        services.AddHostedService<BankPayoutReconciliationWorker>();

        // Registered as IFiatEventHandler ONLY. It credits nothing, so it must never satisfy the
        // live-boot IFiatSettlementHandler probe -- that marker is what proves a node can credit a
        // card before it is allowed to charge one, and a payout handler is not that proof.
        services.AddScoped<IFiatEventHandler, PayoutReversalEventHandler>();

        // The durable saga: one definition plus its three step handlers. Without the handlers the
        // processor claims a step and throws resolving it, so they are registered together here
        // and never separately.
        services.AddSingleton<ISagaDefinition>(provider =>
            OffRampSagaDefinition.Build(
                provider.GetRequiredService<OffRampOptions>().EffectiveMaxPayoutAttempts));

        services.AddScoped<IStepHandler<OffRampCollectPayload>, OffRampCollectStepHandler>();
        services.AddScoped<IStepHandler<OffRampPayPayload>, OffRampPayStepHandler>();
        services.AddScoped<IStepHandler<OffRampReturnPayload>, OffRampReturnStepHandler>();

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
}
