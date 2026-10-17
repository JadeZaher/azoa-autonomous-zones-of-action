using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

internal static class HoldReleaseWorkflowFence
{
    internal static IReadOnlyList<SurrealQuery>? Create(HoldReleaseMandate mandate)
    {
        if (mandate.WorkflowSourceJson is null) return null;
        var source = JsonSerializer.Deserialize<HoldReleaseWorkflowSource>(mandate.WorkflowSourceJson);
        if (source is null || source.Version != 1 || !Guid.TryParseExact(source.ExecutionRecordId, "N", out var executionId)
            || executionId == Guid.Empty || source.Witness is null || source.Witness.Version != 1
            || string.IsNullOrWhiteSpace(source.OpeningCommandId) || source.OpeningIntentHash.Length != 64
            || source.OpeningOutcomeHash.Length != 64) return null;
        var witness = source.Witness; var action = witness.Action;
        if (action.RunId == Guid.Empty || action.NodeId == Guid.Empty || action.IntentHash.Length != 64
            || action.ActorUserId.ToString("N") != mandate.ActorUserId || action.ActingTenantId?.ToString("N") != mandate.TenantId
            || !witness.RequiredScopes.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { AzoaScopes.ActionExecute, AzoaScopes.TransferSign }.Order(StringComparer.Ordinal))) return null;
        var authority = SigningAuthorityFence.ForNodeAdmission(witness, "hold_workflow_mandate");
        if (authority is null) return null;
        var actor = SurrealLink.ToLink("user", mandate.ActorUserId)!;
        var tenant = SurrealLink.ToLink("user", mandate.TenantId);
        var run = SurrealLink.ToLink("action_run", action.RunId.ToString("N"))!;
        var node = SurrealLink.ToLink("action_node", action.NodeId.ToString("N"))!;
        var signing = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness));
        var result = authority.ToList();
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ActionRun>(action.RunId.ToString("N"))
            .Where(row => row.UserId == actor && row.ActingTenantId == tenant
                && (row.Status == ActionRun.ActionRunStatusKind.Running || row.Status == ActionRun.ActionRunStatusKind.AwaitingReconciliation
                    || row.Status == ActionRun.ActionRunStatusKind.Succeeded))
            .Set(row => row.UserId, actor).Build(), "hold_workflow_run", "_hold_workflow_run"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ActionNodeExecution>(source.ExecutionRecordId)
            .Where(row => row.RunId == run && row.NodeId == node && row.State == ActionNodeExecution.ActionNodeState.Succeeded
                && row.SigningAuthorityJson == signing)
            .Set(row => row.State, ActionNodeExecution.ActionNodeState.Succeeded).Build(), "hold_workflow_node", "_hold_workflow_node"));
        // lock: command receipts are create-only; the run, node and liability CAS updates serialize this release.
        result.Add(SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<HoldCommandReceipt>.Key(source.OpeningCommandId),
            "hold_workflow_receipt", "_hold_workflow_receipt"));
        result.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(mandate.HoldRecordId)
            .Where(row => row.OpeningCommandId == source.OpeningCommandId && row.ActiveReleaseMandateId == mandate.Id)
            .Set(row => row.OpeningCommandId, source.OpeningCommandId).Build(), "hold_workflow_liability", "_hold_workflow_liability"));
        // raw: only the exact completed opening and immutable accounting receipt can authorize this delayed source after run completion.
        result.Add(SurrealQuery.Of("""
            IF $_hold_workflow_run = NONE OR $_hold_workflow_run = [] OR $_hold_workflow_node = NONE OR $_hold_workflow_node = []
                OR $_hold_workflow_node.frozen_input = NONE OR crypto::sha256($_hold_workflow_node.frozen_input) != $_hold_workflow_hash
                OR $_hold_workflow_liability = NONE OR $_hold_workflow_liability = []
                OR $_hold_workflow_receipt = NONE OR $_hold_workflow_receipt.intent_hash != $_hold_workflow_open_hash
                OR $_hold_workflow_receipt.actor_user_id != $_hold_workflow_actor
                OR (!$_hold_workflow_has_tenant AND $_hold_workflow_receipt.tenant_id != NONE)
                OR ($_hold_workflow_has_tenant AND $_hold_workflow_receipt.tenant_id != $_hold_workflow_tenant)
                OR crypto::sha256($_hold_workflow_receipt.outcome_json) != $_hold_workflow_outcome_hash
                { THROW 'SIGNING_AUTHORITY_CHANGED' }
            """).WithParam("_hold_workflow_hash", action.IntentHash).WithParam("_hold_workflow_open_hash", source.OpeningIntentHash)
            .WithParam("_hold_workflow_outcome_hash", source.OpeningOutcomeHash)
            .WithParam("_hold_workflow_actor", mandate.ActorUserId).WithParam("_hold_workflow_has_tenant", mandate.TenantId is not null)
            .WithParam("_hold_workflow_tenant", mandate.TenantId ?? string.Empty));
        return result;
    }
}
