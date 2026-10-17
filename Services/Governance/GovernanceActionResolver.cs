using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Recovers atomic election commands using their immutable action input.</summary>
public sealed class GovernanceActionResolver(ConsumerGovernanceService governance) : IActionExternalOperationResolver
{
    /// <inheritdoc/>
    public string Domain => "governance";

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        if (reference.Version != 1 || reference.Domain != Domain || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash)
            return new(ActionExternalOperationStatus.Ambiguous, reference, Detail: "Invalid election recovery identity.");
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput);
        if (input is null || input.Version != 1 || input.ActorUserId != reference.ActorUserId || input.ActingTenantId != reference.ActingTenantId
            || input.NodeType is not (ActionNodeType.GovernanceProposalCreate or ActionNodeType.GovernanceVote or ActionNodeType.GovernanceTally))
            return new(ActionExternalOperationStatus.Ambiguous, reference, Detail: "Invalid election recovery scope.");
        var action = new ActionDefinition { Nodes = [new ActionNode { Id = input.NodeId, NodeType = input.NodeType, Config = input.Config.GetRawText() }] };
        var context = new ActionNodeExecutionContext(input.RunId, input.NodeId, action, input.ActorUserId, input.Upstream,
            input.ActingTenantId, input.AllExecutions);
        var expected = GovernanceCommandIdentity.Reference(context, reference.IntentHash);
        if (expected != reference) return new(ActionExternalOperationStatus.Ambiguous, reference, Detail: "Election receipt correlation changed.");
        var result = input.NodeType switch
        {
            ActionNodeType.GovernanceProposalCreate => await new GovernanceProposalCreateNodeHandler(governance).HandleAsync(context, ct),
            ActionNodeType.GovernanceVote => await new GovernanceVoteNodeHandler(governance).HandleAsync(context, ct),
            _ => await new GovernanceTallyNodeHandler(governance).HandleAsync(context, ct)
        };
        if (result.IsError && !result.Retriable) result = await governance.RecordInvalidAsync(context, result, ct);
        return result.IsError ? new(result.Retriable ? ActionExternalOperationStatus.Ambiguous : ActionExternalOperationStatus.Failed, reference, Detail: result.Message)
            : new(ActionExternalOperationStatus.Succeeded, reference, result.Output);
    }
}
