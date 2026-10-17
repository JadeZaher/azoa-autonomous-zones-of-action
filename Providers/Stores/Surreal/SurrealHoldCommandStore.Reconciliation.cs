using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<HoldCommand>> ListReconciliationAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
        => await executor.QueryAsync<HoldCommand>(SurrealQuery<HoldCommand>.From()
            .Where(row => row.State == "Submitted" || ((row.State == "Admitted" || row.State == "Preparing") && row.AcceptanceState == "Submitted"))
            .Where(row => row.NextReconcileAt == null || row.NextReconcileAt <= now)
            .OrderBy(row => row.NextReconcileAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 100)), ct);

    /// <inheritdoc/>
    public async Task<bool> ScheduleReconciliationAsync(HoldCommand expected, DateTimeOffset next, CancellationToken ct = default)
    {
        var query = SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.AcceptanceState == expected.AcceptanceState
                && row.NextReconcileAt == expected.NextReconcileAt)
            .Set(row => row.NextReconcileAt, (DateTimeOffset?)next).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
        }, ct);
        return response.GetValues<HoldCommand>(0).Any();
    }
}
