using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Read-only SurrealDB <see cref="IConsentGrantStore"/>; grants are written only by
/// <see cref="SurrealConsentLifecycleStore"/> (see `Providers/Stores/Surreal/AGENTS.md` §consent-grant-store).</summary>
public sealed class SurrealConsentGrantStore : IConsentGrantStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealConsentGrantStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ConsentGrant>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(SurrealId.ToSurrealId(id)), ct);
        return new AZOAResult<ConsentGrant>
        {
            Result = row is null ? null : ToDomain(row),
            Message = row is null ? "Consent grant not found." : "Success",
            IsError = row is null,
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByGrantorAsync(Guid grantorUserId, CancellationToken ct = default)
    {
        var grantor = UserLink(grantorUserId);
        return Ok(await _executor.QueryAsync<Row>(SurrealQuery<Row>.From()
            .Where(row => row.GrantorUserId == grantor).OrderByDescending(row => row.GrantedAt), ct));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = UserLink(tenantId);
        return Ok(await _executor.QueryAsync<Row>(SurrealQuery<Row>.From()
            .Where(row => row.TenantId == tenant).OrderByDescending(row => row.GrantedAt), ct));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ConsentGrant>> FindCoveringGrantAsync(
        Guid grantorUserId, Guid tenantId, string scope, DateTime now, CancellationToken ct = default)
    {
        var grantor = UserLink(grantorUserId);
        var tenant = UserLink(tenantId);
        var instant = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        var rows = await _executor.QueryAsync<Row>(SurrealQuery<Row>.From()
            .Where(row => row.GrantorUserId == grantor && row.TenantId == tenant && row.RevokedAt == null
                && (row.ExpiresAt == null || row.ExpiresAt > instant)), ct);
        // Liveness is filtered server-side; Covers() owns the final scope word (a miss fails closed).
        return new AZOAResult<ConsentGrant> { Result = rows.Select(ToDomain).FirstOrDefault(grant => grant.Covers(scope, now)), Message = "Success" };
    }

    private static string UserLink(Guid userId) => SurrealLink.ToLink("user", SurrealId.ToSurrealId(userId))!;

    private static AZOAResult<IEnumerable<ConsentGrant>> Ok(IReadOnlyList<Row> rows)
        => new() { Result = rows.Select(ToDomain).ToList(), Message = "Success" };

    private static ConsentGrant ToDomain(Row row) => new()
    {
        Id = SurrealRecordGuid.Parse(row.Id),
        GrantorUserId = SurrealRecordGuid.Parse(row.GrantorUserId),
        TenantId = SurrealRecordGuid.Parse(row.TenantId),
        Scopes = string.IsNullOrWhiteSpace(row.Scopes) ? new List<string>()
            : row.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Origin = Enum.TryParse<GrantOrigin>(row.Origin, ignoreCase: true, out var origin) ? origin : GrantOrigin.UserExplicit,
        ParticipationRef = row.ParticipationRef,
        GrantedAt = row.GrantedAt.UtcDateTime,
        ExpiresAt = row.ExpiresAt?.UtcDateTime,
        RevokedAt = row.RevokedAt?.UtcDateTime,
    };
}
