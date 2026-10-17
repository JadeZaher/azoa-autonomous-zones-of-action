using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Blockchain.ClientSigning;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealOperationLogClientSigningStore(ISurrealExecutor executor) : IClientSignedOperationStore
{
    private const int MaxStuckPage = 200;

    /// <inheritdoc/>
    public async Task<bool> TryTransitionAsync(Guid operationId, string expectedStatus, string nextStatus,
        IReadOnlyDictionary<string, string> parameters, DateTimeOffset? completedAt, CancellationToken ct = default)
    {
        var expected = Enum.Parse<OperationLog.StatusKind>(expectedStatus);
        var next = Enum.Parse<OperationLog.StatusKind>(nextStatus);
        // lock: the operation row itself; every client-signing transition changes status, so the status CAS serializes writers.
        var update = SurrealWriter.UpdateOnly<OperationLog>(SurrealId.ToSurrealId(operationId))
            .Where(row => row.Status == expected)
            .Set(row => row.Status, next)
            .Set(row => row.Parameters, JsonSerializer.SerializeToElement(parameters));
        update = completedAt is { } completed ? update.Set(row => row.CompletedDate, completed) : update.Unset(row => row.CompletedDate);
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(update.Build(), ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        return response.GetValues<OperationLog>(0).Any();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Guid>> ListStuckAsync(DateTimeOffset awaitingBefore, int limit, CancellationToken ct = default)
    {
        const OperationLog.StatusKind awaiting = OperationLog.StatusKind.AwaitingSignature;
        const OperationLog.StatusKind unconfirmed = OperationLog.StatusKind.PendingConfirmation;
        const OperationLog.StatusKind parked = OperationLog.StatusKind.NeedsOperator;
        // PendingConfirmation is shared with custodial rows, so the page over-fetches and the service keeps rows with a saved plan.
        var rows = await executor.QueryAsync<OperationLog>(SurrealQuery<OperationLog>.From()
            .Where(row => ((row.Status == awaiting || row.Status == unconfirmed) && row.CreatedDate < awaitingBefore) || row.Status == parked)
            .OrderBy(row => row.CreatedDate).Limit(Math.Clamp(limit * 4, 1, MaxStuckPage)), ct);
        return rows.Select(row => SurrealId.FromSurrealId(row.Id)).ToArray();
    }
}
