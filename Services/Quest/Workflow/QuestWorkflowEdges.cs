using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest.Workflow;

/// <summary>
/// The single authority for "what is the next node in a durable run's DAG".
/// Both advancement paths — the engine's auto-advance
/// (<see cref="QuestNodeStepHandler"/>) and the consumer's manual
/// <c>advance(...)</c> (<c>QuestManager.AdvanceAsync</c>) — resolve the next hop
/// through here, so the DAG-walk rule and the fork-merge non-goal live in ONE
/// place and can never drift between the two paths.
/// </summary>
public static class QuestWorkflowEdges
{
    /// <summary>The outgoing Control-edge target node ids of <paramref name="nodeId"/>
    /// (the forward hop). Conditional edges are not forward hops — they gate
    /// failed-predecessor skipping, the same role they play in the in-process
    /// executor.</summary>
    public static IReadOnlyList<Guid> NextControlNodeIds(Models.Quest.Quest quest, Guid nodeId) =>
        quest.Edges
            .Where(e => e.SourceNodeId == nodeId && e.EdgeType == QuestEdgeType.Control)
            .Select(e => e.TargetNodeId)
            .Distinct()
            .ToList();

    /// <summary>
    /// Resolve the single next hop, enforcing the fork-merge non-goal in one
    /// place: a node has zero successors (<see cref="SuccessorKind.Terminal"/>),
    /// exactly one (<see cref="SuccessorKind.Single"/> — the normal hop), or more
    /// than one (<see cref="SuccessorKind.FanOut"/> — rejected, fork-merge is out
    /// of scope). Both advancement paths switch on the result so neither can
    /// accept a fan-out the other rejects.
    /// </summary>
    public static SuccessorResolution ResolveSingleSuccessor(Models.Quest.Quest quest, Guid nodeId)
    {
        var successors = NextControlNodeIds(quest, nodeId);
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
