// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Managers;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// DI wiring for per-project custodial treasuries; called from <c>Program.cs</c> (W3.1
/// owns that file and calls this as a fully-qualified static).
/// </summary>
public static class ProjectTreasuryRegistration
{
    /// <summary>
    /// Registers <see cref="IProjectTreasury"/> and its ledger store. Throws at startup
    /// when the custody seam, the reserve options or the SurrealDB executor it depends on
    /// are absent, so a half-wired container fails loudly at boot rather than quietly
    /// booting a node where funding a project silently reaches no account.
    /// </summary>
    public static IServiceCollection AddProjectTreasury(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(descriptor => descriptor.ServiceType != typeof(IKeyCustodyService)))
            throw new InvalidOperationException(
                "AddProjectTreasury requires IKeyCustodyService to be registered first: a project treasury " +
                "has no secret of its own and derives its key from the chain's platform key.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(ReserveOptions)))
            throw new InvalidOperationException(
                "AddProjectTreasury requires the bound ReserveOptions instance to be registered first: the " +
                "operator's approved-chain list gates project treasuries exactly as it gates the reserve.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(ISurrealExecutor)))
            throw new InvalidOperationException(
                "AddProjectTreasury requires ISurrealExecutor to be registered first (AddSurrealForge).");

        services.AddScoped<IProjectTreasuryLedgerStore, SurrealProjectTreasuryLedgerStore>();
        services.AddScoped<IProjectTreasury, ProjectTreasuryService>();
        return services;
    }
}
