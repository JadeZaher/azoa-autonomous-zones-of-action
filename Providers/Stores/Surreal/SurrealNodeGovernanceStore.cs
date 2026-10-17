using SurrealForge.Client.Query;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealNodeGovernanceStore : INodeGovernanceStore
{
    private const string ParametersTable = NodeGovernanceParameters.SchemaNameConst;

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
        if (parameters.Id != NodeGovernanceParameters.LocalId)
            return AZOAResult<NodeGovernanceParameters>.Failure("Node governance policy must use the local singleton identity.");
        // raw: governance CAS and immutable audit append share one transaction.
        // lock: the policy row read here is upserted below in this batch, and NodeGovernancePolicyFence.Lock() writes the policy lock row first.
        var readVersion = SurrealQuery
            .Of("LET $_versions = SELECT VALUE version FROM type::record($_pt, $_pid)")
            .WithParam("_pt", ParametersTable)
            .WithParam("_pid", NodeGovernanceParameters.LocalId);

        var enforceVersion = SurrealQuery
            .Of("IF ($_expect_existing AND (array::len($_versions) != 1 OR $_versions[0] != $_expected)) OR (!$_expect_existing AND array::len($_versions) != 0) { THROW 'Node governance parameters version conflict' }")
            .WithParam("_expect_existing", expectedVersion.HasValue)
            .WithParam("_expected", expectedVersion ?? 0L);

        var statements = new List<SurrealQuery>
        {
            SurrealQuery.Of("BEGIN"), NodeGovernancePolicyFence.Lock(), readVersion, enforceVersion,
            SurrealGeneratedStatementScope.Apply(SurrealWriter.Upsert(parameters), "policy_write")
        };
        if (parameters.AllowedChains is null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<NodeGovernanceParameters>(NodeGovernanceParameters.LocalId)
                .Where(row => row.Version == parameters.Version)
                .Unset(row => row.AllowedChains).Build(), "policy_chains"));
        if (parameters.AllowedAssetTypes is null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<NodeGovernanceParameters>(NodeGovernanceParameters.LocalId)
                .Where(row => row.Version == parameters.Version)
                .Unset(row => row.AllowedAssetTypes).Build(), "policy_assets"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "policy_audit"));
        var resultIndex = statements.Count;
        statements.Add(SurrealQuery<NodeGovernanceParameters>.Key(NodeGovernanceParameters.LocalId));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var atomic = SurrealQuery.Combine(statements.ToArray());

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

            var persisted = response.GetValues<NodeGovernanceParameters>(resultIndex).SingleOrDefault()
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
