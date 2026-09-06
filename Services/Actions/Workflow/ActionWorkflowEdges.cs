using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Workflow;

/// <summary>
/// The single authority for "what is the next node in a durable run's DAG".
/// Both advancement paths — the engine's auto-advance
/// (<see cref="ActionNodeStepHandler"/>) and the consumer's manual
/// <c>advance(...)</c> (<c>ActionManager.AdvanceAsync</c>) — resolve the next hop
/// through here, so the DAG-walk rule and the fork-merge non-goal live in ONE
/// place and can never drift between the two paths.
/// </summary>
public static class ActionWorkflowEdges
{
    /// <summary>The outgoing Control-edge target node ids of <paramref name="nodeId"/>
    /// (the forward hop). Conditional edges are not forward hops — they gate
    /// failed-predecessor skipping, the same role they play in the in-process
    /// executor.</summary>
    public static IReadOnlyList<Guid> NextControlNodeIds(Models.Actions.ActionDefinition action, Guid nodeId) =>
        action.Edges
            .Where(e => e.SourceNodeId == nodeId && e.EdgeType == ActionEdgeType.Control)
            .Select(e => e.TargetNodeId)
            .Distinct()
            .ToList();

    /// <summary>
    /// Outgoing OnFailure-edge target node ids for <paramref name="nodeId"/>.
    /// Used by the durable engine to advance on the failure arm when a node
    /// terminally fails (V5, Services/Actions/Workflow/AGENTS.md §skip-semantics).
    /// </summary>
    public static IReadOnlyList<Guid> OnFailureNodeIds(Models.Actions.ActionDefinition action, Guid nodeId) =>
        action.Edges
            .Where(e => e.SourceNodeId == nodeId && e.EdgeType == ActionEdgeType.OnFailure)
            .Select(e => e.TargetNodeId)
            .Distinct()
            .ToList();

    /// <summary>
    /// Resolves the OnFailure successor for a terminally-failed node.
    /// Returns <see cref="SuccessorKind.Terminal"/> when no OnFailure edge exists,
    /// <see cref="SuccessorKind.Single"/> for exactly one, or
    /// <see cref="SuccessorKind.FanOut"/> for >1 (publish validator blocks this).
    /// </summary>
    public static SuccessorResolution ResolveOnFailureSuccessor(Models.Actions.ActionDefinition action, Guid nodeId)
    {
        var targets = OnFailureNodeIds(action, nodeId);
        return targets.Count switch
        {
            0 => new SuccessorResolution(SuccessorKind.Terminal, null, 0),
            1 => new SuccessorResolution(SuccessorKind.Single, targets[0], 1),
            _ => new SuccessorResolution(SuccessorKind.FanOut, null, targets.Count),
        };
    }

    /// <summary>
    /// Resolve the single next hop, enforcing the fork-merge non-goal in one
    /// place: a node has zero successors (<see cref="SuccessorKind.Terminal"/>),
    /// exactly one (<see cref="SuccessorKind.Single"/> — the normal hop), or more
    /// than one (<see cref="SuccessorKind.FanOut"/> — rejected, fork-merge is out
    /// of scope). Both advancement paths switch on the result so neither can
    /// accept a fan-out the other rejects.
    /// </summary>
    public static SuccessorResolution ResolveSingleSuccessor(Models.Actions.ActionDefinition action, Guid nodeId)
    {
        var successors = NextControlNodeIds(action, nodeId);
        return successors.Count switch
        {
            0 => new SuccessorResolution(SuccessorKind.Terminal, null, 0),
            1 => new SuccessorResolution(SuccessorKind.Single, successors[0], 1),
            _ => new SuccessorResolution(SuccessorKind.FanOut, null, successors.Count),
        };
    }
}

/// <summary>Whether a node has zero / one / many outgoing Control successors.</summary>
public enum SuccessorKind
{
    /// <summary>No Control successors — a terminal node; the run completes.</summary>
    Terminal,

    /// <summary>Exactly one Control successor — the normal forward hop.</summary>
    Single,

    /// <summary>More than one Control successor — rejected (fork-merge out of scope).</summary>
    FanOut
}

/// <summary>The resolved next hop (the fork-merge guard lives in the resolver).</summary>
/// <param name="Kind">Terminal / Single / FanOut.</param>
/// <param name="NodeId">The single successor when <see cref="Kind"/> is
/// <see cref="SuccessorKind.Single"/>; otherwise <c>null</c>.</param>
/// <param name="Count">The successor count (for the fan-out error message).</param>
public sealed record SuccessorResolution(SuccessorKind Kind, Guid? NodeId, int Count);
