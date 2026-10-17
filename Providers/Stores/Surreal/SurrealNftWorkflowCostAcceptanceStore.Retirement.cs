using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using ExecutionRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealNftWorkflowCostAcceptanceStore
{
    /// <inheritdoc/>
    public async Task<bool> RetireAsync(NftWorkflowCostAcceptance request, SigningConsentWitness consent, CancellationToken ct = default)
    {
        var credential = consent.Credential;
        if (request.RequestId == Guid.Empty || request.ExecutionId == Guid.Empty || request.Authorization is null
            || request.ExpectedExecutionVersion < 0 || request.ExpectedExecutionVersion == long.MaxValue) return false;
        var id = NftWorkflowCostAcceptanceIdentity.Id(credential.ActorUserId, credential.TenantId, request.ExecutionId, request.RequestId);
        var requestJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request));
        var requestHash = CanonicalJson.Hash(request);
        var existing = await executor.QuerySingleAsync<NftWorkflowCostRetirement>(SurrealQuery<NftWorkflowCostRetirement>.Key(id), ct);
        if (existing is not null) return existing.ActorUserId == credential.ActorUserId.ToString("N")
            && existing.ActingTenantId == credential.TenantId?.ToString("N") && existing.ExecutionId == request.ExecutionId.ToString("N")
            && existing.ExecutionVersion == request.ExpectedExecutionVersion && existing.RequestJson == requestJson && existing.RequestHash == requestHash;
        var execution = await executor.QuerySingleAsync<ExecutionRow>(SurrealQuery<ExecutionRow>.Key(request.ExecutionId.ToString("N")), ct);
        if (execution?.ExternalOperationJson is not { } referenceJson || execution.FrozenInput is not { } frozen
            || execution.Version != request.ExpectedExecutionVersion || execution.State != ExecutionRow.ActionNodeState.Running) return false;
        var reference = JsonSerializer.Deserialize<ActionExternalOperationReference>(referenceJson);
        if (reference is not { Version: 1, Domain: "nft" } || reference.ActorUserId != credential.ActorUserId
            || reference.ActingTenantId != credential.TenantId || NftWorkflowMintDraft.Read(frozen, reference) is null) return false;
        var fence = SigningAuthorityFence.ForConsent(consent, [AzoaScopes.NftMint], "nft_retire_current");
        if (fence is null) return false;
        var receipt = new NftWorkflowCostRetirement { Id = id, ActorUserId = credential.ActorUserId.ToString("N"),
            ActingTenantId = credential.TenantId?.ToString("N"), ExecutionId = request.ExecutionId.ToString("N"),
            ExecutionVersion = request.ExpectedExecutionVersion, RequestJson = requestJson, RequestHash = requestHash,
            ReferenceJson = referenceJson, ConsentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent)),
            CreatedAt = DateTimeOffset.UtcNow };
        // raw: retirement and admission contend on one execution version; the audit and version advance commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fence);
        statements.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<NftWorkflowCostAcceptanceRecord>.Key(id), "nft_retire_audit", "_nft_retire_audit"));
        statements.Add(SurrealQuery.Of("IF $_nft_retire_audit != NONE { THROW 'NFT_WORKFLOW_COST_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExecutionRow>(receipt.ExecutionId)
            .Where(row => row.Version == request.ExpectedExecutionVersion && row.State == ExecutionRow.ActionNodeState.Running
                && row.ExternalOperationJson == referenceJson && row.FrozenInput == frozen && row.SigningAuthorityJson == execution.SigningAuthorityJson)
            .Set(row => row.Version, request.ExpectedExecutionVersion + 1).Build(), "nft_retire_execution", "_nft_retire_execution"));
        statements.Add(SurrealQuery.Of("IF $_nft_retire_execution = NONE OR $_nft_retire_execution = [] { THROW 'NFT_WORKFLOW_COST_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "nft_retire_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()), ct);
                response.EnsureAllOk(); return response;
            }, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var winner = await executor.QuerySingleAsync<NftWorkflowCostRetirement>(SurrealQuery<NftWorkflowCostRetirement>.Key(id), ct);
            return winner?.RequestJson == requestJson && winner.RequestHash == requestHash;
        }
    }
}
