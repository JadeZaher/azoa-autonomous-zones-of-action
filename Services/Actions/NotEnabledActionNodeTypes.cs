using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Explicit activation gates; see Services/Governance/AGENTS.md §governance-gate and round2-governance-source-review.md.</summary>
public static class NotEnabledActionNodeTypes
{
    // Governance lifts only with RUNTIME_VERDICT: ACCEPTED in round4-governance-runtime-verdict.md.
    private static readonly HashSet<ActionNodeType> Blocked =
    [
        ActionNodeType.CredentialRevoke,
        ActionNodeType.GovernanceProposalCreate,
        ActionNodeType.GovernanceVote,
        ActionNodeType.GovernanceTally,
    ];

    public static bool IsBlocked(ActionNodeType nodeType) => Blocked.Contains(nodeType);
    public static IReadOnlySet<ActionNodeType> All => Blocked;
    public static string RefusalMessage(ActionNodeType nodeType) =>
        $"Node type '{nodeType}' awaits its required authority and durable recovery review. See conductor/tracks.md.";
}
