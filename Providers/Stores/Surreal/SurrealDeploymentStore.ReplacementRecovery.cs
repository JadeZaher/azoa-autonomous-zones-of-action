using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealDeploymentStore
{
    /// <inheritdoc/>
    public async Task<DeploymentOperation?> ReplaceRecoveryChildAsync(DeploymentOperation parent, DeploymentOperation predecessor,
        DeploymentOperation replacement, CancellationToken ct = default)
    {
        var proof = NativeRecoveryReplacement.ProofKind(predecessor);
        if (proof is null || parent.ParentOperationId is not null || parent.RecoveryChildId != predecessor.Id
            || parent.RecoveryBindingHash != predecessor.RecoveryBindingHash || parent.PreparedJson is not null || parent.SubmissionAuthorized
            || predecessor.ParentOperationId != parent.Id || replacement.ParentOperationId != parent.Id
            || replacement.ReplacesOperationId != predecessor.Id || replacement.Id == predecessor.Id || replacement.Id == parent.Id
            || replacement.ActorUserId != parent.ActorUserId || predecessor.ActorUserId != parent.ActorUserId
            || replacement.TenantId != parent.TenantId || predecessor.TenantId != parent.TenantId
            || replacement.ArtifactId != parent.ArtifactId || replacement.ArtifactHash != parent.ArtifactHash
            || predecessor.ArtifactId != parent.ArtifactId || predecessor.ArtifactHash != parent.ArtifactHash
            || replacement.RecoveryMode is not ("AbortCleanup" or "ContinueCleanup" or "FinalizeAuthority")
            || string.IsNullOrWhiteSpace(replacement.RecoveryBindingHash) || replacement.ReplacedByOperationId is not null
            || replacement.RecoveryProjected || replacement.SubmissionAuthorized || replacement.PreparedJson is not null
            || replacement.RecoveryChildId is not null || replacement.ActionRunId is not null
            || replacement.State != "Admitted" || replacement.Generation != 0 || replacement.HistoryJson != "[]")
            return null;
        var quote = JsonSerializer.Deserialize<DeploymentStartRequest>(replacement.RequestJson)?.NativeIntent?.Quote;
        if (quote is null) return null;
        var quoteJson = JsonSerializer.Serialize(quote);
        var now = DateTimeOffset.UtcNow;
        var oldMutation = SurrealWriter.UpdateOnly<DeploymentOperation>(predecessor.Id)
            .Where(row => row.Version == predecessor.Version && row.ParentOperationId == parent.Id
                && row.RecoveryBindingHash == predecessor.RecoveryBindingHash && row.RequestJson == predecessor.RequestJson
                && row.HistoryJson == predecessor.HistoryJson && row.Generation == predecessor.Generation
                && row.State == predecessor.State && row.PreparedJson == predecessor.PreparedJson
                && !row.SubmissionAuthorized && !row.RecoveryProjected && row.ReplacedByOperationId == null
                && row.ActorUserId == parent.ActorUserId && row.TenantId == parent.TenantId)
            .Set(row => row.ReplacedByOperationId, replacement.Id).Set(row => row.Version, predecessor.Version + 1)
            .Set(row => row.UpdatedAt, now).Build();
        var parentMutation = SurrealWriter.UpdateOnly<DeploymentOperation>(parent.Id)
            .Where(row => row.Version == parent.Version && row.Generation == parent.Generation
                && row.RecoveryChildId == predecessor.Id && row.RecoveryBindingHash == predecessor.RecoveryBindingHash
                && row.RequestJson == parent.RequestJson && row.HistoryJson == parent.HistoryJson
                && row.ActorUserId == parent.ActorUserId && row.TenantId == parent.TenantId
                && row.ParentOperationId == null && row.PreparedJson == null && !row.SubmissionAuthorized)
            .Set(row => row.RecoveryChildId, replacement.Id).Set(row => row.RecoveryBindingHash, replacement.RecoveryBindingHash)
            .Set(row => row.RecoveryMode, replacement.RecoveryMode).Set(row => row.Version, parent.Version + 1)
            .Set(row => row.UpdatedAt, now).Build();
        var quoteMutation = SurrealWriter.UpdateOnly<NativeQuote>(quote.QuoteId)
            .Where(row => row.ActorUserId == parent.ActorUserId && row.TenantId == parent.TenantId
                && row.IntentHash == quote.IntentHash && row.QuoteJson == quoteJson)
            .Set(row => row.IntentHash, quote.IntentHash).Build();
        // raw: exact quote, predecessor retirement, parent link swap and new child create commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        foreach (var entry in new[] { ("replacement_quote", quoteMutation), ("replacement_old", oldMutation), ("replacement_parent", parentMutation) })
        {
            var mutation = SurrealGeneratedStatementScope.Apply(entry.Item2, entry.Item1);
            statements.Add(SurrealQuery.Of("LET $_" + entry.Item1 + " = (" + mutation.Sql + ")").WithParams(mutation.Params));
            statements.Add(SurrealQuery.Of("IF $_" + entry.Item1 + " = NONE OR $_" + entry.Item1 + " = [] { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"));
            if (entry.Item1 == "replacement_quote")
                statements.Add(SurrealQuery.Of("IF $_replacement_quote.expires_at <= time::now() { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(replacement), "replacement_new"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()), ct);
            response.EnsureAllOk();
            return await GetAsync(replacement.Id, ct);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(RecoveryConflict, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetAsync(replacement.Id, ct);
            return existing?.ParentOperationId == parent.Id && existing.ReplacesOperationId == predecessor.Id
                && existing.ActorUserId == replacement.ActorUserId && existing.TenantId == replacement.TenantId
                && existing.IntentHash == replacement.IntentHash && existing.RequestJson == replacement.RequestJson
                && existing.RecoveryBindingHash == replacement.RecoveryBindingHash ? existing : null;
        }
    }
}
