using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealNftWorkflowCostAcceptanceStore(ISurrealExecutor executor) : INftWorkflowCostAcceptanceStore
{
    private const string Conflict = "NFT_WORKFLOW_COST_CHANGED";

    /// <inheritdoc/>
    public async Task<NftWorkflowCostAcceptanceRecord?> GetAsync(Guid actor, Guid? tenant, Guid executionId, Guid requestId, CancellationToken ct = default)
    {
        var id = NftWorkflowCostAcceptanceIdentity.Id(actor, tenant, executionId, requestId);
        var row = await executor.QuerySingleAsync<NftWorkflowCostAcceptanceRecord>(SurrealQuery<NftWorkflowCostAcceptanceRecord>.Key(id), ct);
        if (row is null) return null;
        var receipt = JsonSerializer.Deserialize<NftWorkflowCostAcceptanceReceipt>(row.ReceiptJson)
            ?? throw new InvalidOperationException("NFT acceptance receipt is unreadable.");
        if (!NftWorkflowCostAcceptanceIdentity.Matches(receipt) || row.Id != id || row.ActorUserId != actor.ToString("N")
            || row.ActingTenantId != tenant?.ToString("N") || row.ExecutionId != executionId.ToString("N")
            || receipt.Request.ExecutionId != executionId || receipt.Request.RequestId != requestId
            || receipt.Reference.ActorUserId != actor || receipt.Reference.ActingTenantId != tenant
            || row.ExecutionVersion != receipt.Request.ExpectedExecutionVersion || row.LifecycleId != receipt.Reference.OperationId
            || row.FrozenInputHash != receipt.Reference.IntentHash || row.CreatedAt != receipt.CreatedAt
            || row.RequestHash != NftWorkflowCostAcceptanceIdentity.RequestHash(receipt.Request, row.LifecycleId, row.FrozenInputHash)
            || row.ReceiptJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(receipt))
            || row.ReceiptHash != CanonicalJson.Hash(receipt))
            throw new InvalidOperationException("NFT acceptance receipt integrity differs from its immutable binding.");
        return row;
    }

    /// <inheritdoc/>
    public async Task<NftWorkflowCostAcceptanceRecord?> ClaimAsync(NftLifecycle candidate, NftWorkflowCostAcceptanceReceipt receipt, CancellationToken ct = default)
    {
        var request = receipt.Request;
        var reference = receipt.Reference;
        var credential = receipt.AcceptingAuthority.Credential;
        if (reference.ActorUserId != credential.ActorUserId || reference.ActingTenantId != credential.TenantId
            || reference.OperationId is null) return null;
        var hash = NftWorkflowCostAcceptanceIdentity.RequestHash(request, reference.OperationId, reference.IntentHash);
        var prior = await GetAsync(credential.ActorUserId, credential.TenantId, request.ExecutionId, request.RequestId, ct);
        if (prior is not null) return prior.RequestHash == hash ? prior : null;
        if (!NftWorkflowCostAcceptanceIdentity.Matches(receipt)) return null;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(candidate.RequestJson)
            ?? throw new InvalidOperationException("NFT candidate is unreadable.");
        if (!NftProtocolCostAdmissionFence.Matches(candidate, command) || candidate.Id != reference.OperationId
            || command.ActionAuthority != receipt.OriginalAuthority.Action || command.ProtocolCostQuote != receipt.Quote
            || command.Actor != reference.ActorUserId || command.Recipient != reference.ActorUserId
            || command.WorkflowCostAcceptance is null || CanonicalJson.Hash(command.WorkflowCostAcceptance) != CanonicalJson.Hash(receipt)) return null;
        if (command.Metadata.IsSoulbound || command.FeeQuote is null || !Enum.TryParse<ChainNetwork>(candidate.Network, out var network)) return null;
        var initialIntent = NftLifecycleIntentFactory.Create(candidate, command, network, null, command.ActingTenantId);
        if (initialIntent is null || NftProtocolCostContract.IntentHash(initialIntent, command.RecipientWalletId) != receipt.Quote.QuotedIntentHash) return null;
        var execution = await executor.QuerySingleAsync<ActionNodeExecution>(SurrealQuery<ActionNodeExecution>.Key(request.ExecutionId.ToString("N")), ct);
        if (execution?.FrozenInput is not { } frozen || NftWorkflowMintDraft.Read(frozen, reference) is not { } draft
            || NftProtocolCostContract.DraftHash(candidate.Id, command.Actor, command.Recipient, draft) != receipt.Quote.DraftHash) return null;
        var original = SigningAuthorityFence.ForNodeAdmission(receipt.OriginalAuthority, "nft_accept_original");
        var accepting = SigningAuthorityFence.ForConsent(receipt.AcceptingAuthority, [AzoaScopes.NftMint], "nft_accept_current");
        var actor = command.ActorAuthority;
        if (actor is null || actor.Action != receipt.OriginalAuthority.Action || actor.Wallet.Id != command.ActorWalletId
            || actor.Wallet.Address != command.ActorAddress || actor.Wallet.ChainType != candidate.ChainType) return null;
        var walletAuthority = SigningAuthorityFence.Create(actor, [AzoaScopes.NftMint], "nft_accept_wallet");
        if (original is null || accepting is null || walletAuthority is null) return null;
        var audit = new NftWorkflowCostAcceptanceRecord
        {
            Id = NftWorkflowCostAcceptanceIdentity.Id(credential.ActorUserId, credential.TenantId, request.ExecutionId, request.RequestId),
            ActorUserId = credential.ActorUserId.ToString("N"), ActingTenantId = credential.TenantId?.ToString("N"),
            ExecutionId = request.ExecutionId.ToString("N"), ExecutionVersion = request.ExpectedExecutionVersion,
            LifecycleId = candidate.Id, FrozenInputHash = reference.IntentHash, RequestHash = hash,
            ReceiptJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(receipt)),
            ReceiptHash = CanonicalJson.Hash(receipt), CreatedAt = receipt.CreatedAt,
        };
        var action = receipt.OriginalAuthority.Action;
        var run = SurrealLink.ToLink("action_run", action.RunId.ToString("N"))!;
        var node = SurrealLink.ToLink("action_node", action.NodeId.ToString("N"))!;
        var signing = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(receipt.OriginalAuthority));
        var referenceJson = JsonSerializer.Serialize(reference);
        // raw: exact execution, original and current consent, fresh offer, acceptance audit and NFT admission commit atomically.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(original);
        statements.AddRange(accepting);
        statements.AddRange(walletAuthority);
        statements.AddRange(ActionExecutionFence.Create(action.RunId.ToString("N"), action.NodeId.ToString("N"),
            credential.ActorUserId.ToString("N"), credential.TenantId?.ToString("N"), reference.IntentHash));
        statements.AddRange(NftProtocolCostAdmissionFence.Create(receipt.Quote));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ActionNodeExecution>(audit.ExecutionId)
            .Where(row => row.Version == request.ExpectedExecutionVersion && row.RunId == run && row.NodeId == node
                && row.State == ActionNodeExecution.ActionNodeState.Running && row.SigningAuthorityJson == signing
                && row.ExternalOperationJson == referenceJson)
            .Set(row => row.Version, request.ExpectedExecutionVersion + 1).Build(), "nft_accept_execution", "_nft_accept_execution"));
        statements.Add(SurrealQuery.Of("""
            IF $_nft_accept_execution = NONE OR $_nft_accept_execution = []
                OR $_nft_accept_execution.frozen_input = NONE
                OR crypto::sha256($_nft_accept_execution.frozen_input) != $_nft_accept_frozen_hash
                { THROW 'NFT_WORKFLOW_COST_CHANGED' }
            """).WithParam("_nft_accept_frozen_hash", reference.IntentHash));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "nft_accept_audit"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(candidate), "nft_accept_lifecycle"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var query = SurrealQuery.Combine(statements.ToArray());
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(query, ct);
                result.EnsureAllOk();
                return result;
            }, ct);
            return await GetAsync(credential.ActorUserId, credential.TenantId, request.ExecutionId, request.RequestId, ct)
                ?? throw new InvalidOperationException("NFT acceptance disappeared after commit.");
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(NftProtocolCostAdmissionFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await GetAsync(credential.ActorUserId, credential.TenantId, request.ExecutionId, request.RequestId, ct);
            return prior?.RequestHash == hash ? prior : null;
        }
    }
}
