using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealNodeTreasuryStore : INodeTreasuryStore
{
    private const string DestinationTable = NodeTreasuryDestination.SchemaNameConst;
    private const string AuditTable = NodeTreasuryAudit.SchemaNameConst;

    private readonly ISurrealExecutor _executor;

    public SurrealNodeTreasuryStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeTreasuryDestination?>> GetDestinationAsync(
        string chain,
        ChainNetwork network,
        CancellationToken ct = default)
    {
        var id = NodeTreasuryDestination.RecordIdFor(chain, network.ToString());
        var query = SurrealQuery<NodeTreasuryDestination>.Key(id);
        var row = await _executor.QuerySingleAsync<NodeTreasuryDestination>(query, ct);
        return new AZOAResult<NodeTreasuryDestination?>
        {
            Result = row,
            Message = row is null ? "Not configured." : "Success",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeTreasuryDestination>> UpdateDestinationWithAuditAsync(
        NodeTreasuryDestination destination,
        NodeTreasuryAudit audit,
        long? expectedVersion,
        CancellationToken ct = default)
    {
        // raw: destination CAS and immutable audit append share one transaction.
        var readVersion = SurrealQuery
            .Of("LET $_versions = SELECT VALUE version FROM type::record($_dt, $_did)")
            .WithParam("_dt", DestinationTable)
            .WithParam("_did", destination.Id);

        var enforceVersion = SurrealQuery
            .Of("IF ($_expect_existing AND (array::len($_versions) != 1 OR $_versions[0] != $_expected)) OR (!$_expect_existing AND array::len($_versions) != 0) { THROW 'Node treasury destination version conflict' }")
            .WithParam("_expect_existing", expectedVersion.HasValue)
            .WithParam("_expected", expectedVersion ?? 0L);

        var upsertDestination = SurrealQuery
            .Of("UPSERT type::record($_dt, $_did) SET chain = type::string($_chain), network = type::string($_network), address = array::join($_address_chars, ''), version = $_version, updated_by_avatar_id = $_actor, updated_at = $_updated_at RETURN AFTER")
            .WithParam("_dt", DestinationTable)
            .WithParam("_did", destination.Id)
            .WithParam("_chain", destination.Chain)
            .WithParam("_network", destination.Network)
            .WithParam("_address_chars", destination.Address.Select(c => c.ToString()).ToArray())
            .WithParam("_version", destination.Version)
            .WithParam("_actor", destination.UpdatedByAvatarId)
            .WithParam("_updated_at", destination.UpdatedAt);

        var appendAudit = SurrealQuery
            .Of("CREATE type::record($_at, $_aid) CONTENT $_audit RETURN AFTER")
            .WithParam("_at", AuditTable)
            .WithParam("_aid", audit.Id)
            .WithParam("_audit", audit);

        var atomic = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            readVersion,
            enforceVersion,
            upsertDestination,
            appendAudit,
            SurrealQuery.Of("COMMIT"));

        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(atomic, ct);
                var errors = string.Join(" ", executed.Where(statement => !statement.IsOk)
                    .Select(statement => statement.ErrorText));
                if (errors.Contains(
                        "Node treasury destination version conflict",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new TreasuryVersionConflictException(
                        "Node treasury destination version conflict");
                }
                if (SurrealTransientConflict.IsRetryableConflict(
                        new InvalidOperationException(errors)))
                    throw new TreasuryVersionConflictException("Transaction conflict");

                executed.EnsureAllOk();
                return executed;
            }, ct);

            var persisted = response.GetValues<NodeTreasuryDestination>(3).SingleOrDefault()
                ?? throw new InvalidOperationException("Treasury destination write returned no row.");
            return new AZOAResult<NodeTreasuryDestination>
            {
                Result = persisted,
                Message = "Saved.",
            };
        }
        catch (TreasuryVersionConflictException)
        {
            return AZOAResult<NodeTreasuryDestination>.Failure(
                "Node treasury destination version conflict.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<NodeTreasuryAudit>>> ListAuditAsync(
        int limit,
        CancellationToken ct = default)
    {
        var query = SurrealQuery<NodeTreasuryAudit>
            .From()
            .OrderByDescending(audit => audit.OccurredAt)
            .Limit(limit);
        var rows = await _executor.QueryAsync<NodeTreasuryAudit>(query, ct);
        return new AZOAResult<IEnumerable<NodeTreasuryAudit>>
        {
            Result = rows,
            Message = "Success",
        };
    }

    private sealed class TreasuryVersionConflictException(string message) : Exception(message);
}
