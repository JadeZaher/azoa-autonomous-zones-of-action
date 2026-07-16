using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealAdminBootstrapStateStore : IAdminBootstrapStateStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealAdminBootstrapStateStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<AdminBootstrapState?>> GetAsync(CancellationToken ct = default)
    {
        var row = await _executor.QuerySingleAsync<AdminBootstrapState>(
            SurrealQuery<AdminBootstrapState>.Key(AdminBootstrapState.LocalId), ct);
        return AZOAResult<AdminBootstrapState?>.Success(row, row is null ? "Not bootstrapped." : "Success");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<AdminBootstrapState>> BindOnceAsync(
        AdminBootstrapState state,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var response = await _executor.ExecuteAsync(SurrealWriter.Create(state), ct);
            response.EnsureAllOk();
            var created = response.GetValues<AdminBootstrapState>(0).SingleOrDefault()
                ?? throw new InvalidOperationException("Bootstrap binding write returned no row.");
            return AZOAResult<AdminBootstrapState>.Success(created, "Bootstrap binding created.");
        }
        catch (SurrealStatementException)
        {
            var existing = await GetAsync(ct);
            if (existing.IsError || existing.Result is null)
                throw;

            return AZOAResult<AdminBootstrapState>.Success(existing.Result, "Bootstrap binding already exists.");
        }
    }
}
