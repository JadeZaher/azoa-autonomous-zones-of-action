using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldReleaseMandateStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<HoldReleaseMandate>> ListOwnerAsync(Guid actor, Guid? tenant, int offset, int limit,
        CancellationToken ct = default)
    {
        if (actor == Guid.Empty || tenant == Guid.Empty || offset < 0 || limit is < 1 or > 100) return [];
        var actorId = actor.ToString("N"); var tenantId = tenant?.ToString("N");
        return await executor.QueryAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.From()
            .Where(row => row.ActorUserId == actorId && row.TenantId == tenantId)
            .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Start(offset).Limit(limit), ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HoldReleaseMandate>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
        => await executor.QueryAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.From()
            .Where(row => row.Status == "Active" && row.NotBefore <= now && row.ExpiresAt > now)
            .Where(row => row.NextAttemptAt == null || row.NextAttemptAt <= now)
            .OrderBy(row => row.NextAttemptAt!).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 100)), ct);

    /// <inheritdoc/>
    public async Task<bool> ScheduleAsync(HoldReleaseMandate expected, DateTimeOffset next, CancellationToken ct = default)
    {
        var query = SurrealWriter.UpdateOnly<HoldReleaseMandate>(expected.Id)
            .Where(row => row.Version == expected.Version && row.Status == "Active" && row.NextAttemptAt == expected.NextAttemptAt
                && row.OperationId == expected.OperationId)
            .Set(row => row.NextAttemptAt, (DateTimeOffset?)next).Build();
        var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
        }, ct);
        return result.GetValues<HoldReleaseMandate>(0).Any();
    }
}
