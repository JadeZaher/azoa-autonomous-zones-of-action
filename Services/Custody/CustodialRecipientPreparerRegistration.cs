// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Custody;

/// <summary>DI wiring for custodial recipient preparation; called from <c>Program.cs</c>.</summary>
public static class CustodialRecipientPreparerRegistration
{
    /// <summary>
    /// Registers <see cref="ICustodialRecipientPreparer"/>. Throws at startup when the
    /// provider factory it reads rails through is absent, so a half-wired container fails
    /// loudly at boot rather than quietly refusing every issuance at run time with a
    /// "rail unavailable" a tenant would read as a chain outage.
    /// </summary>
    public static IServiceCollection AddCustodialRecipientPreparation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(descriptor => descriptor.ServiceType != typeof(IBlockchainProviderFactory)))
            throw new InvalidOperationException(
                "AddCustodialRecipientPreparation requires IBlockchainProviderFactory to be registered first.");

        services.AddScoped<ICustodialRecipientPreparer, CustodialRecipientPreparer>();
        return services;
    }
}
