using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Rechecks the accepted receipt and original workflow signing authority before new effects.</summary>
internal static class NftWorkflowCostAuthorityFence
{
    internal static IReadOnlyList<SurrealQuery> Create(NftLifecycle row, NftLifecycleCommand command)
    {
        var receipt = command.WorkflowCostAcceptance;
        if (receipt is null || !NftWorkflowCostAcceptanceIdentity.Matches(receipt)
            || receipt.Reference.OperationId != row.Id || receipt.OriginalAuthority.Action != command.ActionAuthority
            || receipt.Quote != command.ProtocolCostQuote || receipt.Reference.ActorUserId != command.Actor
            || (receipt.Reference.ActingTenantId ?? Guid.Empty) != command.ActingTenantId)
            return Refused();
        var authority = SigningAuthorityFence.ForNodeAdmission(receipt.OriginalAuthority, "nft_workflow_original");
        if (authority is null) return Refused();
        var id = NftWorkflowCostAcceptanceIdentity.Id(command.Actor, receipt.Reference.ActingTenantId,
            receipt.Request.ExecutionId, receipt.Request.RequestId);
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(receipt));
        var result = authority.ToList();
        var action = receipt.OriginalAuthority.Action;
        var run = SurrealLink.ToLink("action_run", action.RunId.ToString("N"))!;
        var node = SurrealLink.ToLink("action_node", action.NodeId.ToString("N"))!;
        var signing = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(receipt.OriginalAuthority));
        var referenceJson = JsonSerializer.Serialize(receipt.Reference);
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ActionNodeExecution>(receipt.Request.ExecutionId.ToString("N"))
            .Where(execution => execution.RunId == run && execution.NodeId == node
                && execution.State == ActionNodeExecution.ActionNodeState.Running && execution.SigningAuthorityJson == signing
                && execution.ExternalOperationJson == referenceJson)
            .Set(execution => execution.State, ActionNodeExecution.ActionNodeState.Running).Build(),
            "nft_workflow_execution", "_nft_workflow_execution"));
        result.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<NftWorkflowCostAcceptanceRecord>.Key(id),
            "nft_workflow_acceptance", "_nft_workflow_acceptance"));
        // raw: exact accepted receipt and original credential/grants participate in each new lifecycle effect.
        result.Add(SurrealQuery.Of("""
            IF $_nft_workflow_execution = NONE OR $_nft_workflow_execution = []
                OR crypto::sha256($_nft_workflow_execution.frozen_input) != $_nft_workflow_frozen_hash
                OR $_nft_workflow_acceptance = NONE
                OR $_nft_workflow_acceptance.lifecycle_id != $_nft_workflow_lifecycle
                OR $_nft_workflow_acceptance.receipt_json != $_nft_workflow_receipt
                OR $_nft_workflow_acceptance.receipt_hash != $_nft_workflow_receipt_hash
                { THROW 'ACTION_EXECUTION_AUTHORITY_CHANGED' }
            """).WithParam("_nft_workflow_lifecycle", row.Id).WithParam("_nft_workflow_receipt", json)
                .WithParam("_nft_workflow_frozen_hash", receipt.Reference.IntentHash)
                .WithParam("_nft_workflow_receipt_hash", CanonicalJson.Hash(receipt)));
        return result;
    }

    private static IReadOnlyList<SurrealQuery> Refused()
        => [SurrealQuery.Of("THROW 'ACTION_EXECUTION_AUTHORITY_CHANGED'")];
}
