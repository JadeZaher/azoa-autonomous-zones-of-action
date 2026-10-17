using SurrealForge.Client.Query;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealNodeFeeScheduleStore : INodeFeeScheduleStore
{
    private const string ConflictMarker = "Node fee schedule version conflict";

    private readonly ISurrealExecutor _executor;

    public SurrealNodeFeeScheduleStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSchedule?>> GetScheduleAsync(CancellationToken ct = default)
    {
        var query = SurrealQuery<NodeFeeSchedule>.Key(NodeFeeSchedule.LocalId);
        var row = await _executor.QuerySingleAsync<NodeFeeSchedule>(query, ct);
        return AZOAResult<NodeFeeSchedule?>.Success(
            row,
            row is null ? "Not configured." : "Success");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSchedule>> UpdateScheduleWithAuditAsync(
        NodeFeeSchedule schedule,
        NodeFeeAudit audit,
        long? expectedVersion,
        CancellationToken ct = default)
    {
        if (schedule.Id != NodeFeeSchedule.LocalId || schedule.Version != (expectedVersion ?? 0) + 1)
            return AZOAResult<NodeFeeSchedule>.Failure(ConflictMarker);
        SurrealQuery mutation;
        if (expectedVersion is null)
            mutation = SurrealWriter.Create(schedule);
        else
        {
            var update = SurrealWriter.UpdateOnly<NodeFeeSchedule>(NodeFeeSchedule.LocalId)
                .Where(row => row.Version == expectedVersion.Value)
                .Set(row => row.MintFlatBaseUnits, schedule.MintFlatBaseUnits).Set(row => row.MintBps, schedule.MintBps)
                .Set(row => row.TransferFlatBaseUnits, schedule.TransferFlatBaseUnits).Set(row => row.TransferBps, schedule.TransferBps)
                .Set(row => row.SwapFlatBaseUnits, schedule.SwapFlatBaseUnits).Set(row => row.SwapBps, schedule.SwapBps)
                .Set(row => row.ActionCompleteFlatBaseUnits, schedule.ActionCompleteFlatBaseUnits).Set(row => row.ActionCompleteBps, schedule.ActionCompleteBps)
                .Set(row => row.FederationPublishFlatBaseUnits, schedule.FederationPublishFlatBaseUnits).Set(row => row.FederationPublishBps, schedule.FederationPublishBps)
                .Set(row => row.Version, schedule.Version).Set(row => row.UpdatedAt, schedule.UpdatedAt);
            update = schedule.SwapFlatDenomination is null ? update.Unset(row => row.SwapFlatDenomination)
                : update.Set(row => row.SwapFlatDenomination, schedule.SwapFlatDenomination);
            update = schedule.UpdatedByUserId is null ? update.Unset(row => row.UpdatedByUserId)
                : update.Set(row => row.UpdatedByUserId, schedule.UpdatedByUserId);
            mutation = update.Build();
        }
        var captured = SurrealGeneratedStatementScope.Assign(mutation, "fee_policy", "_fee_saved");
        var appendAudit = SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "fee_audit");
        // raw: captured typed policy CAS and immutable audit append commit or roll back together.
        var atomic = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            captured,

            SurrealQuery.Of("IF $_fee_saved = NONE OR $_fee_saved = [] { THROW 'Node fee schedule version conflict' }"),
            appendAudit, SurrealQuery.Of("RETURN $_fee_saved"), SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(atomic, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);
            var persisted = response.GetValues<NodeFeeSchedule>(4).SingleOrDefault()
                ?? throw new InvalidOperationException("Fee schedule write returned no row.");
            return AZOAResult<NodeFeeSchedule>.Success(persisted, "Saved.");
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains(ConflictMarker, StringComparison.Ordinal) ||
            SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return AZOAResult<NodeFeeSchedule>.Failure(ConflictMarker);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<NodeFeeAudit>>> ListAuditAsync(int limit, CancellationToken ct = default)
    {
        var query = SurrealQuery<NodeFeeAudit>
            .From()
            .OrderByDescending(audit => audit.OccurredAt)
            .Limit(limit);
        var rows = await _executor.QueryAsync<NodeFeeAudit>(query, ct);
        return AZOAResult<IEnumerable<NodeFeeAudit>>.Success(rows);
    }

}
