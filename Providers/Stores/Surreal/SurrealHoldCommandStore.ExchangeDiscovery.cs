using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    private static readonly string[] ExchangeReconcilableStates =
        ["Committed", "Preparing", "Prepared", "Submitted", "Confirmed", "ExpiredAbsent"];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExchangeHoldSource>> ListExchangeReconciliationAsync(DateTimeOffset now, int limit,
        CancellationToken ct = default)
        => await executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From()
            .Where(row => ExchangeReconcilableStates.Contains(row.State))
            .Where(row => row.NextReconcileAt == null || row.NextReconcileAt <= now)
            .OrderBy(row => row.NextReconcileAt!).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 100)), ct);

    /// <inheritdoc/>
    public async Task<bool> ScheduleExchangeReconciliationAsync(ExchangeHoldSource expected, DateTimeOffset next,
        CancellationToken ct = default)
    {
        var query = SurrealWriter.UpdateOnly<ExchangeHoldSource>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.NextReconcileAt == expected.NextReconcileAt)
            .Set(row => row.NextReconcileAt, (DateTimeOffset?)next).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
        }, ct);
        return response.GetValues<ExchangeHoldSource>(0).Any();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExchangeHoldSource>> ListExchangeSourcesAsync(Guid tenant, Guid apiKey, int offset, int limit,
        CancellationToken ct = default)
    {
        var tenantId = tenant.ToString("N"); var keyId = apiKey.ToString("N");
        return await executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From()
            .Where(row => row.TenantUserId == tenantId && row.ApiKeyId == keyId)
            .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id)
            .Start(Math.Max(0, offset)).Limit(Math.Clamp(limit, 1, 100)), ct);
    }
}
