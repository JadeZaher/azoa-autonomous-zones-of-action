using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealNodeTransparencyStore : INodeTransparencyStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealNodeTransparencyStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<NodeTreasuryDestination>>> ListTreasuryDestinationsAsync(
        int limit,
        CancellationToken ct = default)
    {
        var query = SurrealQuery<NodeTreasuryDestination>
            .From()
            .OrderBy(destination => destination.Chain)
            .Limit(limit);
        var rows = await _executor.QueryAsync<NodeTreasuryDestination>(query, ct);
        return Success(rows.ToList());
    }

    /// <inheritdoc/>
    public Task<AZOAResult<IReadOnlyList<NodeGovernanceAudit>>> ListGovernanceAuditAsync(
        int limit,
        NodeTransparencyStoreCursor? before,
        CancellationToken ct = default)
        => ListAuditAsync<NodeGovernanceAudit>(NodeGovernanceAudit.SchemaNameConst, limit, before, ct);

    /// <inheritdoc/>
    public Task<AZOAResult<IReadOnlyList<NodeFeeAudit>>> ListFeeAuditAsync(
        int limit,
        NodeTransparencyStoreCursor? before,
        CancellationToken ct = default)
        => ListAuditAsync<NodeFeeAudit>(NodeFeeAudit.SchemaNameConst, limit, before, ct);

    /// <inheritdoc/>
    public Task<AZOAResult<IReadOnlyList<NodeTreasuryAudit>>> ListTreasuryAuditAsync(
        int limit,
        NodeTransparencyStoreCursor? before,
        CancellationToken ct = default)
        => ListAuditAsync<NodeTreasuryAudit>(NodeTreasuryAudit.SchemaNameConst, limit, before, ct);

    private async Task<AZOAResult<IReadOnlyList<T>>> ListAuditAsync<T>(
        string table,
        int limit,
        NodeTransparencyStoreCursor? before,
        CancellationToken ct)
        where T : ISurrealRecord, new()
    {
        // raw: composite keyset pagination; owner=SurrealForge.Client, expires=2026-09-30; see the code-style track.
        var query = before is null
            ? SurrealQuery
                .Of("SELECT * FROM type::table($_t) ORDER BY occurred_at DESC, id DESC LIMIT $_limit")
                .WithParam("_t", table)
                .WithParam("_limit", limit)
            : SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE occurred_at < $_occurred_at OR (occurred_at = $_occurred_at AND id < type::record($_t, $_id)) ORDER BY occurred_at DESC, id DESC LIMIT $_limit")
                .WithParam("_t", table)
                .WithParam("_occurred_at", before.OccurredAt)
                .WithParam("_id", SurrealRecordGuid.BareId(before.RecordId))
                .WithParam("_limit", limit);
        var rows = await _executor.QueryAsync<T>(query, ct);
        return Success(rows.ToList());
    }

    private static AZOAResult<IReadOnlyList<T>> Success<T>(IReadOnlyList<T> rows)
        => new() { Result = rows, Message = "Success" };
}
