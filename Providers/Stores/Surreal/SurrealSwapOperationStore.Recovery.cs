using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealSwapOperationStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<SwapOperationRecord>> ReadRecoveryDueAsync(int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) return [];
        var now = DateTimeOffset.UtcNow;
        return await executor.QueryAsync<SwapOperationRecord>(SurrealQuery<SwapOperationRecord>.From()
            .Where(row => row.NextAttemptAt <= now && (row.State == "Pending" || row.State == "NeedsOperator" ||
                row.State == "StageResolved" || row.UnresolvedRentStageCount > 0))
            .OrderBy(row => row.NextAttemptAt).ThenBy(row => row.Id).Limit(limit), ct);
    }

    /// <inheritdoc/>
    public async Task<SwapOperationRecord?> ClaimRecoveryAsync(string operationId, long expectedVersion, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var query = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId)
            .Where(row => row.Version == expectedVersion && row.NextAttemptAt <= now)
            .Set(row => row.NextAttemptAt, now.AddMinutes(1)).Set(row => row.Version, expectedVersion + 1)
            .Set(row => row.UpdatedAt, now).Build();
        var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return response;
        }, ct);
        return result.GetValues<SwapOperationRecord>(0).SingleOrDefault();
    }

    /// <inheritdoc/>
    public async Task<bool> SaveRecoveryCursorAsync(string operationId, long expectedVersion, string? cursor, CancellationToken ct = default)
    {
        var mutation = SurrealWriter.UpdateOnly<SwapOperationRecord>(operationId).Where(row => row.Version == expectedVersion)
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow);
        mutation = cursor is null ? mutation.Unset(row => row.RecoveryHistoryCursor) : mutation.Set(row => row.RecoveryHistoryCursor, cursor);
        var query = mutation.Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        return response.GetValues<SwapOperationRecord>(0).Count == 1;
    }
}
