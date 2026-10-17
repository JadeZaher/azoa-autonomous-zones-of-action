using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealDeploymentStore
{
    /// <inheritdoc/>
    public async Task<DeploymentOperation?> InvalidateReplacementQuoteAsync(DeploymentOperation parent, DeploymentOperation predecessor,
        CancellationToken ct = default)
    {
        if (parent.ParentOperationId is not null || parent.RecoveryChildId != predecessor.Id
            || predecessor.ParentOperationId != parent.Id || parent.RecoveryBindingHash != predecessor.RecoveryBindingHash
            || parent.ActorUserId != predecessor.ActorUserId || parent.TenantId != predecessor.TenantId
            || NativeRecoveryReplacement.ProofKind(predecessor) is null || parent.PreparedJson is not null || parent.SubmissionAuthorized)
            return null;
        var old = SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<DeploymentOperation>(predecessor.Id)
            .Where(row => row.Version == predecessor.Version && row.RequestJson == predecessor.RequestJson
                && row.HistoryJson == predecessor.HistoryJson && row.State == predecessor.State
                && row.Generation == predecessor.Generation && row.PreparedJson == predecessor.PreparedJson
                && row.ParentOperationId == parent.Id && row.RecoveryBindingHash == predecessor.RecoveryBindingHash
                && row.ActorUserId == parent.ActorUserId && row.TenantId == parent.TenantId
                && !row.SubmissionAuthorized && !row.RecoveryProjected && row.ReplacedByOperationId == null)
            .Set(row => row.Version, predecessor.Version).Build(), "withdrawal_old");
        var advance = SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<DeploymentOperation>(parent.Id)
            .Where(row => row.Version == parent.Version && row.Generation == parent.Generation
                && row.RequestJson == parent.RequestJson && row.HistoryJson == parent.HistoryJson
                && row.RecoveryChildId == predecessor.Id && row.RecoveryBindingHash == predecessor.RecoveryBindingHash
                && row.ActorUserId == parent.ActorUserId && row.TenantId == parent.TenantId
                && row.ParentOperationId == null && row.PreparedJson == null && !row.SubmissionAuthorized)
            .Set(row => row.Version, parent.Version + 1).Set(row => row.Stage, "RecoveryQuoteInvalidated")
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), "withdrawal_parent");
        // raw: exact predecessor lock and parent quote invalidation must compete with resume/replacement atomically.
        var query = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("LET $_withdrawal_old = (" + old.Sql + ")").WithParams(old.Params),
            SurrealQuery.Of("IF $_withdrawal_old = NONE OR $_withdrawal_old = [] { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"),
            SurrealQuery.Of("LET $_withdrawal_parent = (" + advance.Sql + ")").WithParams(advance.Params),
            SurrealQuery.Of("IF $_withdrawal_parent = NONE OR $_withdrawal_parent = [] { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"),
            SurrealQuery.Of("RETURN $_withdrawal_parent"), SurrealQuery.Of("COMMIT"));
        try
        {
            var result = await executor.ExecuteAsync(query, ct);
            result.EnsureAllOk();
            return result.GetValues<DeploymentOperation>(5).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(RecoveryConflict, StringComparison.Ordinal))
        { return null; }
    }
}
