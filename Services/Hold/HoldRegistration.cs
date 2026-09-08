// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>
/// DI wiring for the HOLD primitive; called from <c>Program.cs</c> (W3.1 owns that file
/// and calls this as a fully-qualified static).
/// </summary>
public static class HoldRegistration
{
    /// <summary>
    /// Registers <see cref="IPlatformHold"/>, its ledger store, its bound options and the
    /// hosted sweep. Throws at startup when anything it moves value through is absent, so
    /// a half-wired container fails loudly at boot rather than booting a node where every
    /// hold refuses at run time and the refusal reads like a chain outage.
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
                "AddPlatformHold requires the bound ReserveOptions instance to be registered first: the " +
                "operator's approved-chain list gates holds exactly as it gates the reserve.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(IWalletStore)))
            throw new InvalidOperationException(
                "AddPlatformHold requires IWalletStore to be registered first: a release has to resolve the " +
                "beneficiary's own account before it can pay anything into it.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(ISurrealExecutor)))
            throw new InvalidOperationException(
                "AddPlatformHold requires ISurrealExecutor to be registered first (AddSurrealForge).");

        // Recipient preparation belongs to the issuance item, which registers it through
        // its own extension. Reaching for that PUBLIC extension when it is absent — rather
        // than re-declaring the binding here — keeps one owner for the preparer while
        // guaranteeing a hold can never be released into an account that cannot receive
        // the asset. Registering it twice is what the guard prevents.
        if (services.All(descriptor => descriptor.ServiceType != typeof(ICustodialRecipientPreparer)))
            services.AddCustodialRecipientPreparation();

        // Bound as an instance resolved from configuration (the ReserveOptions precedent):
        // an absent section must still yield the fail-closed defaults rather than null.
        services.AddSingleton(provider =>
            provider.GetRequiredService<IConfiguration>()
                .GetSection(HoldOptions.SectionName)
                .Get<HoldOptions>()
            ?? new HoldOptions());

        services.AddScoped<IHoldLedgerStore, SurrealHoldLedgerStore>();
        services.AddScoped<IPlatformHold, PlatformHoldService>();

        // The sweep is what makes a hold window a promise the node keeps on its own. It
        // resolves the scoped hold service per cycle; see HoldSweepWorker.
        services.AddHostedService<HoldSweepWorker>();

        return services;
    }
}
