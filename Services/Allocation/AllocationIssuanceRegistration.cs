// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;

namespace AZOA.WebAPI.Services.Allocation;

/// <summary>
/// DI wiring for the issuance allocation path (W3.3); called from <c>Program.cs</c> as a
/// fully-qualified static.
/// </summary>
public static class AllocationIssuanceRegistration
{
    /// <summary>
    /// Registers the two collaborators <see cref="AZOA.WebAPI.Managers.AllocationManager"/>
    /// takes as OPTIONAL constructor parameters and therefore silently does without when
    /// they are absent: recipient preparation (an issuance into a never-used custodial
    /// account) and the durable fee obligation a non-zero transfer fee is admitted into.
    /// Both are optional so the many hand-constructed instances in tests stay short — which
    /// is exactly why the production container has to register them deliberately here.
    /// Registering them is what keeps the issuance path from being a control that exists in
    /// prose and refuses at run time.
    /// </summary>
    public static IServiceCollection AddAllocationIssuance(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(descriptor => descriptor.ServiceType != typeof(IBlockchainProviderFactory)))
            throw new InvalidOperationException(
                "AddAllocationIssuance requires IBlockchainProviderFactory to be registered first: an " +
                "issuance resolves the recipient's rail before it moves a single unit.");

        // Owned by the custody item; reached for through its PUBLIC extension so the
        // preparer keeps one owner. Guarded because the hold item reaches for the same one.
        if (services.All(descriptor => descriptor.ServiceType != typeof(ICustodialRecipientPreparer)))
            services.AddCustodialRecipientPreparation();

        // INodeFeeSettlementStore is registered later in Program.cs; resolution order does
        // not matter to the container, so this binding is deliberately not guarded on it.
        if (services.All(descriptor => descriptor.ServiceType != typeof(INodeFeeSettlementManager)))
        {
            services.AddScoped<INodeFeeSettlementManager,
                AZOA.WebAPI.Managers.NodeFeeSettlementManager>();
        }

        return services;
    }
}
