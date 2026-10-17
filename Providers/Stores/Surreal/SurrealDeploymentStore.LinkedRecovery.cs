using AZOA.WebAPI.Core.Surreal;
using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealDeploymentStore
{
    private const string RecoveryConflict = "DEPLOYMENT_RECOVERY_CONFLICT";

    /// <inheritdoc/>
    public async Task<DeploymentOperation?> CompleteRecoveryChildAsync(string childId, long expectedChildVersion, long expectedParentVersion, CancellationToken ct = default)
    {
        var child = await GetAsync(childId, ct);
        if (child?.ParentOperationId is null) return null;
        var parent = await GetAsync(child.ParentOperationId, ct);
        if (parent?.RecoveryChildId != child.Id || parent.RecoveryBindingHash != child.RecoveryBindingHash) return null;
        if (child.RecoveryProjected) return parent;
        if (child.Version != expectedChildVersion || parent.Version != expectedParentVersion || child.State != "Confirmed"
            || child.PreparedJson is not null || child.SubmissionAuthorized || string.IsNullOrWhiteSpace(child.EvidenceJson)) return null;
        var state = child.RecoveryMode switch
        {
            "AbortCleanup" => "AbortedClean", "ContinueCleanup" => "FailedClean", "FinalizeAuthority" => "Confirmed",
            _ => throw new InvalidOperationException("Unknown linked recovery mode."),
        };
        var evidence = JsonSerializer.Serialize(new { child.Id, child.IntentHash, child.RecoveryBindingHash,
            child.EvidenceJson, child.HistoryJson, child.ResourceId, child.RecoveryMode });
        // raw: child confirmation and parent recovery projection share one exclusive completion transaction.
        var query = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("""
                LET $_completed_child = (UPDATE ONLY type::record('deployment_operation', $_child_id)
                    SET recovery_projected = true, version = version + 1, updated_at = time::now()
                    WHERE version = $_child_version AND state = 'Confirmed' AND recovery_projected = false
                        AND parent_operation_id = $_parent_id AND recovery_binding_hash = $_binding
                        AND evidence_json = $_evidence AND history_json = $_history
                        AND prepared_json = NONE AND submission_authorized = false RETURN AFTER)
                """).WithParam("_child_id", child.Id).WithParam("_child_version", expectedChildVersion)
                .WithParam("_parent_id", parent.Id).WithParam("_binding", child.RecoveryBindingHash)
                .WithParam("_evidence", child.EvidenceJson).WithParam("_history", child.HistoryJson),
            SurrealQuery.Of("IF $_completed_child = NONE { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"),
            SurrealQuery.Of("""
                LET $_completed_parent = (UPDATE ONLY type::record('deployment_operation', $_parent_id)
                    SET recovery_evidence_json = $_projection, state = $_state, stage = 'RecoveryComplete',
                        version = version + 1, updated_at = time::now()
                    WHERE version = $_parent_version AND recovery_child_id = $_child_id AND recovery_binding_hash = $_binding
                        AND actor_user_id = $_actor AND tenant_id = $_tenant AND recovery_mode = $_mode
                        AND prepared_json = NONE AND submission_authorized = false RETURN AFTER)
                """).WithParam("_parent_id", parent.Id).WithParam("_parent_version", expectedParentVersion)
                .WithParam("_child_id", child.Id).WithParam("_binding", child.RecoveryBindingHash)
                .WithParam("_actor", child.ActorUserId).WithParam("_tenant", child.TenantId).WithParam("_mode", child.RecoveryMode)
                .WithParam("_projection", evidence).WithParam("_state", state),
            SurrealQuery.Of("IF $_completed_parent = NONE { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"), SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return await GetAsync(parent.Id, ct);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(RecoveryConflict, StringComparison.Ordinal))
        { return null; }
    }

    /// <inheritdoc/>
    public async Task<DeploymentOperation?> ClaimRecoveryChildAsync(DeploymentOperation parent, DeploymentOperation child, CancellationToken ct = default)
    {
        var quote = JsonSerializer.Deserialize<DeploymentStartRequest>(child.RequestJson)?.NativeIntent?.Quote
            ?? throw new ArgumentException("Recovery child requires an exact stored native quote.");
        if (parent.SubmissionAuthorized || parent.PreparedJson is not null || parent.ParentOperationId is not null
            || child.Id == parent.Id || child.ParentOperationId != parent.Id || child.ActorUserId != parent.ActorUserId
            || child.TenantId != parent.TenantId || child.ArtifactId != parent.ArtifactId || child.ArtifactHash != parent.ArtifactHash
            || child.RecoveryMode is not ("AbortCleanup" or "ContinueCleanup" or "FinalizeAuthority")
            || string.IsNullOrWhiteSpace(child.RecoveryBindingHash) || child.SubmissionAuthorized || child.PreparedJson is not null
            || child.ActionRunId is not null || child.RecoveryChildId is not null)
            throw new ArgumentException("Recovery child does not match a resolved original operation.");
        // raw: exclusive parent recovery linkage and immutable child admission commit together.
        var guard = SurrealQuery.Of("""
            LET $_parent = (UPDATE ONLY type::record('deployment_operation', $_parent_id)
                SET recovery_child_id = $_child_id, recovery_mode = $_mode, recovery_binding_hash = $_binding,
                    version = version + 1, updated_at = time::now()
                WHERE version = $_version AND generation = $_generation AND history_json = $_history
                    AND request_json = $_request AND actor_user_id = $_actor AND tenant_id = $_tenant
                    AND submission_authorized = false AND prepared_json = NONE AND recovery_child_id = NONE
                    AND parent_operation_id = NONE RETURN AFTER)
            """).WithParam("_parent_id", parent.Id).WithParam("_child_id", child.Id)
            .WithParam("_mode", child.RecoveryMode).WithParam("_binding", child.RecoveryBindingHash)
            .WithParam("_version", parent.Version).WithParam("_generation", parent.Generation)
            .WithParam("_history", parent.HistoryJson).WithParam("_request", parent.RequestJson)
            .WithParam("_actor", parent.ActorUserId).WithParam("_tenant", parent.TenantId);
        try
        {
            var quoteGuard = SurrealQuery.Of("""
                LET $_recovery_quote = (UPDATE ONLY type::record('native_quote', $_quote_id) SET intent_hash = intent_hash
                    WHERE actor_user_id = $_quote_actor AND tenant_id = $_quote_tenant AND intent_hash = $_quote_hash
                        AND quote_json = $_quote_json AND expires_at > time::now() RETURN AFTER)
                """).WithParam("_quote_id", quote.QuoteId).WithParam("_quote_actor", child.ActorUserId)
                .WithParam("_quote_tenant", child.TenantId).WithParam("_quote_hash", quote.IntentHash)
                .WithParam("_quote_json", JsonSerializer.Serialize(quote));
            var response = await executor.ExecuteAsync(SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), quoteGuard,
                SurrealQuery.Of("IF $_recovery_quote = NONE { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"), guard,
                SurrealQuery.Of("IF $_parent = NONE { THROW 'DEPLOYMENT_RECOVERY_CONFLICT' }"),
                SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(child), "recovery_child"), SurrealQuery.Of("COMMIT")), ct);
            response.EnsureAllOk();
            return await GetAsync(child.Id, ct) ?? throw new InvalidOperationException("Recovery admission disappeared.");
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(RecoveryConflict, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var current = await GetAsync(parent.Id, ct);
            var existing = await GetAsync(child.Id, ct);
            return current?.RecoveryChildId == child.Id && current.RecoveryBindingHash == child.RecoveryBindingHash
                && existing?.ParentOperationId == parent.Id && existing.IntentHash == child.IntentHash
                && existing.RequestJson == child.RequestJson && existing.RecoveryBindingHash == child.RecoveryBindingHash
                ? existing : null;
        }
    }
}
