using SurrealForge.Client.Query;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealNodeFeeScheduleStore : INodeFeeScheduleStore
{
    private const string ScheduleTable = NodeFeeSchedule.SchemaNameConst;
    private const string AuditTable = NodeFeeAudit.SchemaNameConst;

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
        // raw: schedule CAS and immutable audit append share one transaction.
        var upsertSchedule = SurrealQuery
                .Of("UPSERT type::record($_st, $_sid) SET mint_flat_base_units = type::string($_mint_flat), mint_bps = $_mint_bps, transfer_flat_base_units = type::string($_transfer_flat), transfer_bps = $_transfer_bps, swap_flat_base_units = type::string($_swap_flat), swap_bps = $_swap_bps, quest_complete_flat_base_units = type::string($_quest_flat), quest_complete_bps = $_quest_bps, federation_publish_flat_base_units = type::string($_federation_flat), federation_publish_bps = $_federation_bps, version = $_version, updated_by_avatar_id = $_actor, updated_at = $_updated_at RETURN AFTER")
                .WithParam("_st", ScheduleTable)
                .WithParam("_sid", NodeFeeSchedule.LocalId)
                .WithParam("_mint_flat", schedule.MintFlatBaseUnits)
                .WithParam("_mint_bps", schedule.MintBps)
                .WithParam("_transfer_flat", schedule.TransferFlatBaseUnits)
                .WithParam("_transfer_bps", schedule.TransferBps)
                .WithParam("_swap_flat", schedule.SwapFlatBaseUnits)
                .WithParam("_swap_bps", schedule.SwapBps)
                .WithParam("_quest_flat", schedule.QuestCompleteFlatBaseUnits)
                .WithParam("_quest_bps", schedule.QuestCompleteBps)
                .WithParam("_federation_flat", schedule.FederationPublishFlatBaseUnits)
                .WithParam("_federation_bps", schedule.FederationPublishBps)
                .WithParam("_version", schedule.Version)
                .WithParam("_actor", schedule.UpdatedByAvatarId)
                .WithParam("_updated_at", schedule.UpdatedAt);

        var readVersion = SurrealQuery
                .Of("LET $_versions = SELECT VALUE version FROM type::record($_st, $_sid)")
                .WithParam("_st", ScheduleTable)
                .WithParam("_sid", NodeFeeSchedule.LocalId);

        var enforceVersion = SurrealQuery
                .Of("IF ($_expect_existing AND (array::len($_versions) != 1 OR $_versions[0] != $_expected)) OR (!$_expect_existing AND array::len($_versions) != 0) { THROW 'Node fee schedule version conflict' }")
                .WithParam("_expect_existing", expectedVersion.HasValue)
                .WithParam("_expected", expectedVersion ?? 0L);

        var appendAudit = SurrealQuery
                .Of("CREATE type::record($_at, $_aid) CONTENT $_audit RETURN AFTER")
                .WithParam("_at", AuditTable)
                .WithParam("_aid", audit.Id)
                .WithParam("_audit", audit);

        var atomic = SurrealQuery.Combine(
                SurrealQuery.Of("BEGIN"),
                readVersion,
                enforceVersion,
                upsertSchedule,
                appendAudit,
                SurrealQuery.Of("COMMIT"));

        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(atomic, ct);
                var errors = string.Join(" ", executed.Where(statement => !statement.IsOk)
                    .Select(statement => statement.ErrorText));
                if (errors.Contains("Node fee schedule version conflict", StringComparison.OrdinalIgnoreCase))
                    throw new FeeScheduleVersionConflictException("Node fee schedule version conflict");
                if (SurrealTransientConflict.IsRetryableConflict(
                        new InvalidOperationException(errors)))
                    throw new FeeScheduleVersionConflictException("Transaction conflict");

                executed.EnsureAllOk();
                return executed;
            }, ct);

            var persisted = response.GetValues<NodeFeeSchedule>(3).SingleOrDefault()
                ?? throw new InvalidOperationException("Fee schedule write returned no row.");
            return AZOAResult<NodeFeeSchedule>.Success(persisted, "Saved.");
        }
        catch (FeeScheduleVersionConflictException)
        {
            return AZOAResult<NodeFeeSchedule>.Failure("Node fee schedule version conflict.");
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

    private sealed class FeeScheduleVersionConflictException(string message) : Exception(message);
}
