using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Durable one-time binding for the configured initial node governor.</summary>
public interface IAdminBootstrapStateStore
{
    /// <summary>Loads the local bootstrap binding, if it exists.</summary>
    Task<AZOAResult<AdminBootstrapState?>> GetAsync(CancellationToken ct = default);

    /// <summary>Creates the local binding once; an existing binding is returned for comparison.</summary>
    Task<AZOAResult<AdminBootstrapState>> BindOnceAsync(
        AdminBootstrapState state,
        CancellationToken ct = default);
}
