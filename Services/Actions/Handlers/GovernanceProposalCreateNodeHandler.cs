using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <inheritdoc/>
public sealed class GovernanceProposalCreateNodeHandler(ConsumerGovernanceService governance) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => ActionNodeType.GovernanceProposalCreate;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => GovernanceCommandIdentity.Reference(context, intentHash);

    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => ActionNodeConfig.TryDeserialize<GovernanceProposalCreateNodeConfig>(context.Node.Config, nameof(ActionNodeType.GovernanceProposalCreate), out var config, out var error)
            ? governance.CreateAsync(context, config, ct)
            : Task.FromResult(ActionNodeResults.Invalid(error));
}

public class GovernanceProposalCreateNodeConfig
{
    /// <summary>Human-readable proposal title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What is being decided.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional parent holon (e.g. the co-op the proposal belongs to).</summary>
    public Guid? ParentHolonId { get; set; }

    /// <summary>The ballot choices. At least two distinct, non-empty values.</summary>
    public List<string> Options { get; set; } = new();

    public int MinimumVotes { get; set; }

    /// <summary>Inclusive opening time; defaults to proposal admission.</summary>
    public DateTimeOffset? OpensAt { get; set; }

    /// <summary>When voting closes. Part of the tally preimage, so it pins the tally to a period.</summary>
    public DateTimeOffset? ClosesAt { get; set; }

    /// <summary>Tenant or Global person uniqueness domain.</summary>
    public string? UniquenessScope { get; set; }
}
