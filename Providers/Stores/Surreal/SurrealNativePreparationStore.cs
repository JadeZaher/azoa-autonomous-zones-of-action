using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealNativePreparationStore(ISurrealExecutor executor) : INativePreparationStore
{
    /// <inheritdoc/>
    public Task<NativePreparation?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<NativePreparation>(SurrealQuery<NativePreparation>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<NativePreparation> ClaimAsync(NativePreparation candidate, CancellationToken ct = default)
    {
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
            return response.GetValues<NativePreparation>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return await GetAsync(candidate.Id, ct) ?? throw new InvalidOperationException("Native preparation claim disappeared.");
        }
    }

    /// <inheritdoc/>
    public async Task<NativePreparation> SavePreparedAsync(string id, string intentHash, string preparedJson, CancellationToken ct = default)
    {
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<NativePreparation>(id)
            .Where(row => row.IntentHash == intentHash && row.PreparedJson == null)
            .Set(row => row.PreparedJson, preparedJson).Build(), ct);
        response.EnsureAllOk();
        var current = response.GetValues<NativePreparation>(0).SingleOrDefault() ?? await GetAsync(id, ct)
            ?? throw new InvalidOperationException("Native preparation claim disappeared.");
        if (current.IntentHash != intentHash || current.PreparedJson != preparedJson)
            throw new InvalidOperationException("Native preparation identity or canonical wire changed.");
        return current;
    }
}
