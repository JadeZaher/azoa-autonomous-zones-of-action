using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Finds outputs that exist whenever an occurrence completes successfully.</summary>
public static class DeploymentGuaranteedOutputs
{
    public static IReadOnlySet<Guid> Find(ActionDefinition graph)
    {
        var guaranteed = new HashSet<Guid>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in graph.Nodes)
            {
                if (guaranteed.Contains(node.Id) || graph.Edges.Any(edge => edge.SourceNodeId == node.Id && edge.EdgeType == ActionEdgeType.OnFailure)) continue;
                var incoming = graph.Edges.Where(edge => edge.TargetNodeId == node.Id).ToArray();
                if (incoming.Length == 0 ? node.IsEntry : incoming.All(edge => edge.EdgeType == ActionEdgeType.Control
                    && string.IsNullOrWhiteSpace(edge.Condition) && guaranteed.Contains(edge.SourceNodeId)))
                    changed |= guaranteed.Add(node.Id);
            }
        }
        return guaranteed;
    }
}
