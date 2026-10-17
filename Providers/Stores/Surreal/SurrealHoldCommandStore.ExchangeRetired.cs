using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore : IExchangeRetiredResolutionStore
{
    /// <inheritdoc/>
    public Task<ExchangeRetiredResolution?> GetRetiredResolutionAsync(Guid operationId, CancellationToken ct = default)
        => executor.QuerySingleAsync<ExchangeRetiredResolution>(SurrealQuery<ExchangeRetiredResolution>.Key(operationId.ToString("N")), ct);

    /// <inheritdoc/>
    public async Task<ExchangeRetiredResolution?> RecordRetiredResolutionAsync(ExchangeRetiredResolution resolution, CancellationToken ct = default)
    {
        // lock: create-only on the operation-id record key; the loser re-reads the winner.
        var id = Guid.ParseExact(resolution.Id, "N");
        try
        {
            (await executor.ExecuteAsync(SurrealWriter.Create(resolution), ct)).EnsureAllOk();
        }
        catch (SurrealStatementException)
        {
            if (await GetRetiredResolutionAsync(id, ct) is null) throw;
        }
        var stored = await GetRetiredResolutionAsync(id, ct);
        return stored is not null && stored.Outcome == resolution.Outcome && stored.TransactionHash == resolution.TransactionHash
            && stored.Reason == resolution.Reason ? stored : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OperationLog>> ListRetiredExchangeRowsAsync(int limit, CancellationToken ct = default)
        => await executor.QueryAsync<OperationLog>(SurrealQuery<OperationLog>.From()
            .Where(row => row.OperationType == ReserveExchangeManager.ChainOperationType
                && row.Status != OperationLog.StatusKind.Completed && row.Status != OperationLog.StatusKind.Failed)
            .OrderBy(row => row.CreatedDate).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 200)), ct);
}
