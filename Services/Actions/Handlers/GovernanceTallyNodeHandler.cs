using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <inheritdoc/>
public sealed class GovernanceTallyNodeHandler : IRecoverableActionNodeHandler
{
    private readonly ConsumerGovernanceService _governance;

    public GovernanceTallyNodeHandler(ConsumerGovernanceService governance)
    {
        GovernanceVoteOfRecordGuard.ThrowIfTallyIsNotLedgerOnly();
        _governance = governance;
    }

    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.GovernanceTally;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => GovernanceCommandIdentity.Reference(context, intentHash);

    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => ActionNodeConfig.TryDeserialize<GovernanceTallyNodeConfig>(context.Node.Config, nameof(ActionNodeType.GovernanceTally), out var config, out var error)
            ? _governance.FinalizeAsync(context, config, ct)
            : Task.FromResult(ActionNodeResults.Invalid(error));
}

public class GovernanceTallyNodeConfig
{
    /// <summary>The proposal to count. Usually <c>$from</c>-bound to an upstream GovernanceProposalCreate.</summary>
    public Guid ProposalHolonId { get; set; }
}
