// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Reserve;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>
/// DI wiring for durable custody holds; called from <c>Program.cs</c> as a fully-qualified static.
/// See Services/Hold/AGENTS.md §durable-registration.
/// </summary>
public static class HoldRegistration
{
    /// <summary>
    /// Registers the hold command and release-mandate stores, their services, the frozen action adapter and the two
    /// restricted workers. Throws at startup when a collaborator it accounts through is absent.
    /// </summary>
    public static IServiceCollection AddPlatformHold(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.All(descriptor => descriptor.ServiceType != typeof(IPlatformReserve)))
            throw new InvalidOperationException(
                "AddPlatformHold requires IPlatformReserve to be registered first: a hold's units sit in the " +
                "platform reserve account and its commitment is a draw on the reserve's partition ledger.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(ReserveOptions)))
            throw new InvalidOperationException(
                "AddPlatformHold requires the bound ReserveOptions instance to be registered first.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(IWalletStore)))
            throw new InvalidOperationException(
                "AddPlatformHold requires IWalletStore to be registered first: a release resolves the beneficiary's own account.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(ISurrealExecutor)))
            throw new InvalidOperationException("AddPlatformHold requires ISurrealExecutor to be registered first (AddSurrealForge).");

        services.TryAddScoped<SurrealHoldCommandStore>();
        services.TryAddScoped<IHoldCommandStore>(provider => provider.GetRequiredService<SurrealHoldCommandStore>());
        services.TryAddScoped<IExchangeHoldSourceStore>(provider => provider.GetRequiredService<SurrealHoldCommandStore>());
        services.TryAddScoped<IHoldReleaseMandateStore, SurrealHoldReleaseMandateStore>();
        services.TryAddScoped<HoldRailExecutor>();
        services.TryAddScoped<HoldCommandService>();
        services.TryAddScoped<HoldReleaseMandateService>();
        services.TryAddScoped<ActionHoldOperationService>();
        services.AddScoped<IActionExternalOperationResolver>(provider => provider.GetRequiredService<ActionHoldOperationService>());
        services.AddHostedService<HoldReconciliationWorker>();
        services.AddHostedService<HoldReleaseMandateWorker>();
        return services;
    }
}
