using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Explicit activation gates; see AGENTS.md and round2-governance-source-review.md.</summary>
public static class NotEnabledActionNodeTypes
{
    private static readonly HashSet<ActionNodeType> Blocked = [ActionNodeType.CredentialRevoke];

    public static bool IsBlocked(ActionNodeType nodeType) => Blocked.Contains(nodeType);
    public static IReadOnlySet<ActionNodeType> All => Blocked;
    public static string RefusalMessage(ActionNodeType nodeType) =>
        $"Node type '{nodeType}' awaits its required authority and durable recovery review. See conductor/tracks.md.";
}
