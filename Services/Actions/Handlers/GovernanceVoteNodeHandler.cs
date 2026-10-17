using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <inheritdoc/>
public sealed class GovernanceVoteNodeHandler(ConsumerGovernanceService governance) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.GovernanceVote;
    public const string ProposalUnreadable = "Proposal unavailable to this caller.";

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => GovernanceCommandIdentity.Reference(context, intentHash);

    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => ActionNodeConfig.TryDeserialize<GovernanceVoteNodeConfig>(context.Node.Config, nameof(ActionNodeType.GovernanceVote), out var config, out var error)
            ? governance.VoteAsync(context, config, ct)
            : Task.FromResult(ActionNodeResults.Invalid(error));
}

public class GovernanceVoteNodeConfig
{
    /// <summary>The proposal being voted on. Usually <c>$from</c>-bound to an upstream GovernanceProposalCreate.</summary>
    public Guid ProposalHolonId { get; set; }

    /// <summary>The chosen option. Must be one of the proposal's declared options.</summary>
    public string Option { get; set; } = string.Empty;
}
