using SurrealForge.Client.Query;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealNodeGovernanceStore : INodeGovernanceStore
{
    private const string ParametersTable = NodeGovernanceParameters.SchemaNameConst;
    private const string AuditTable = NodeGovernanceAudit.SchemaNameConst;

    private readonly ISurrealExecutor _executor;

    public SurrealNodeGovernanceStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeGovernanceParameters?>> GetParametersAsync(CancellationToken ct = default)
    {
        var q = SurrealQuery<NodeGovernanceParameters>.Key(NodeGovernanceParameters.LocalId);
        var row = await _executor.QuerySingleAsync<NodeGovernanceParameters>(q, ct);
        return AZOAResult<NodeGovernanceParameters?>.Success(
            row,
            row is null ? "Not configured." : "Success");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeGovernanceParameters>> UpdateParametersWithAuditAsync(
        NodeGovernanceParameters parameters,
        NodeGovernanceAudit audit,
        long? expectedVersion,
        CancellationToken ct = default)
    {
        // raw: governance CAS and immutable audit append share one transaction.
        var readVersion = SurrealQuery
            .Of("LET $_versions = SELECT VALUE version FROM type::record($_pt, $_pid)")
            .WithParam("_pt", ParametersTable)
            .WithParam("_pid", NodeGovernanceParameters.LocalId);

        var enforceVersion = SurrealQuery
            .Of("IF ($_expect_existing AND (array::len($_versions) != 1 OR $_versions[0] != $_expected)) OR (!$_expect_existing AND array::len($_versions) != 0) { THROW 'Node governance parameters version conflict' }")
            .WithParam("_expect_existing", expectedVersion.HasValue)
            .WithParam("_expected", expectedVersion ?? 0L);

        var upsertParameters = SurrealQuery
            .Of("UPSERT type::record($_pt, $_pid) SET allowed_chains = (IF $_has_allowed_chains THEN $_allowed_chains ELSE NONE END), allowed_asset_types = (IF $_has_allowed_asset_types THEN $_allowed_asset_types ELSE NONE END), version = $_version, updated_by_avatar_id = $_actor, updated_at = $_updated_at RETURN AFTER")
            .WithParam("_pt", ParametersTable)
            .WithParam("_pid", NodeGovernanceParameters.LocalId)
            .WithParam("_has_allowed_chains", parameters.AllowedChains is not null)
            .WithParam("_allowed_chains", parameters.AllowedChains ?? Array.Empty<string>())
            .WithParam("_has_allowed_asset_types", parameters.AllowedAssetTypes is not null)
            .WithParam("_allowed_asset_types", parameters.AllowedAssetTypes ?? Array.Empty<string>())
            .WithParam("_version", parameters.Version)
            .WithParam("_actor", parameters.UpdatedByAvatarId)
            .WithParam("_updated_at", parameters.UpdatedAt);

        var appendAudit = SurrealQuery
            .Of("CREATE type::record($_at, $_aid) CONTENT $_audit RETURN AFTER")
            .WithParam("_at", AuditTable)
            .WithParam("_aid", audit.Id)
            .WithParam("_audit", audit);

        var atomic = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            readVersion,
            enforceVersion,
            upsertParameters,
            appendAudit,
            SurrealQuery.Of("COMMIT"));

        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(atomic, ct);
                var errors = string.Join(" ", executed.Where(statement => !statement.IsOk)
                    .Select(statement => statement.ErrorText));
                if (IsExplicitVersionConflict(errors))
                    throw new GovernanceVersionConflictException(
                        "Node governance parameters version conflict");
                if (SurrealTransientConflict.IsRetryableConflict(
                        new InvalidOperationException(errors)))
                    throw new GovernanceVersionConflictException("Transaction conflict");

                executed.EnsureAllOk();
                return executed;
            }, ct);

            var persisted = response.GetValues<NodeGovernanceParameters>(3).SingleOrDefault()
                ?? throw new InvalidOperationException("Governance parameters write returned no row.");
            return AZOAResult<NodeGovernanceParameters>.Success(persisted, "Saved.");
        }
        catch (GovernanceVersionConflictException)
        {
            return AZOAResult<NodeGovernanceParameters>.Failure(
                "Node governance parameters version conflict.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<NodeGovernanceAudit>>> ListAuditAsync(int limit, CancellationToken ct = default)
    {
        var q = SurrealQuery<NodeGovernanceAudit>
            .From()
            .OrderByDescending(audit => audit.OccurredAt)
            .Limit(limit);
        var rows = await _executor.QueryAsync<NodeGovernanceAudit>(q, ct);
        return AZOAResult<IEnumerable<NodeGovernanceAudit>>.Success(rows);
    }

    private static bool IsExplicitVersionConflict(string errors)
        => errors.Contains("Node governance parameters version conflict", StringComparison.OrdinalIgnoreCase);

    private sealed class GovernanceVersionConflictException(string message) : Exception(message);
}
