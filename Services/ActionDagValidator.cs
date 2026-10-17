using AZOA.WebAPI.Interfaces;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;
using ActionEdgeType = AZOA.WebAPI.Models.Actions.ActionEdgeType;

namespace AZOA.WebAPI.Services;

/// <summary>
/// Validates action DAGs using Kahn's algorithm for cycle detection
/// and topological sort, plus entry/terminal/orphan checks.
/// </summary>
public class ActionDagValidator : IActionDagValidator
{
    public DagValidationResult Validate(ActionEntity action, bool fanOutAsError = false)
    {
        var result = new DagValidationResult { IsValid = true };

        if (action.Nodes.Count == 0)
        {
            result.Errors.Add("Action has no nodes.");
            result.IsValid = false;
            return result;
        }

        var nodeIds = action.Nodes.Select(n => n.Id).ToHashSet();

        // Build adjacency list and in-degree count
        var adj = new Dictionary<Guid, List<Guid>>();
        var inDegree = new Dictionary<Guid, int>();
        var incomingEdges = new Dictionary<Guid, HashSet<Guid>>();

        foreach (var node in action.Nodes)
        {
            adj[node.Id] = new List<Guid>();
            inDegree[node.Id] = 0;
            incomingEdges[node.Id] = new HashSet<Guid>();
        }

        foreach (var edge in action.Edges)
        {
            if (!nodeIds.Contains(edge.SourceNodeId))
            {
                result.Errors.Add($"Edge {edge.Id}: SourceNodeId {edge.SourceNodeId} does not exist in action nodes.");
                result.IsValid = false;
                continue;
            }
            if (!nodeIds.Contains(edge.TargetNodeId))
            {
                result.Errors.Add($"Edge {edge.Id}: TargetNodeId {edge.TargetNodeId} does not exist in action nodes.");
                result.IsValid = false;
                continue;
            }

            adj[edge.SourceNodeId].Add(edge.TargetNodeId);
            inDegree[edge.TargetNodeId]++;
            incomingEdges[edge.TargetNodeId].Add(edge.SourceNodeId);
        }

        // Kahn's algorithm: cycle detection + topological sort
        var queue = new Queue<Guid>();
        foreach (var nodeId in inDegree.Keys)
        {
            if (inDegree[nodeId] == 0)
            {
                queue.Enqueue(nodeId);
            }
        }

        var topoOrder = new List<Guid>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            topoOrder.Add(current);

            foreach (var neighbor in adj[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        // Cycle detection
        if (topoOrder.Count != nodeIds.Count)
        {
            var cycleNodes = nodeIds.Except(topoOrder).Select(id =>
                action.Nodes.First(n => n.Id == id).Name).ToList();
            result.Errors.Add($"Cycle detected involving nodes: {string.Join(", ", cycleNodes)}.");
            result.IsValid = false;
            return result;
        }

        // Assign topological order
        for (int i = 0; i < topoOrder.Count; i++)
        {
            var node = action.Nodes.First(n => n.Id == topoOrder[i]);
            node.ExecutionOrder = i;
        }

        result.TopologicalOrder = topoOrder;

        // Entry node check: nodes with no incoming edges that are marked as entry
        var nodesWithNoIncoming = action.Nodes.Where(n => incomingEdges[n.Id].Count == 0).ToList();
        var designatedEntryNodes = nodesWithNoIncoming.Where(n => n.IsEntry).ToList();
        var unmarkedEntryNodes = nodesWithNoIncoming.Where(n => !n.IsEntry).ToList();

        if (nodesWithNoIncoming.Count == 0)
        {
            result.Errors.Add("No entry node found (all nodes have incoming control edges).");
            result.IsValid = false;
        }
        else if (designatedEntryNodes.Count == 0)
        {
            result.Errors.Add($"No node is marked as entry among nodes with no incoming edges: {string.Join(", ", nodesWithNoIncoming.Select(n => n.Name))}.");
            result.IsValid = false;
        }

        // Unmarked nodes with no incoming edges are orphans
        if (unmarkedEntryNodes.Count > 0)
        {
            result.Errors.Add($"Nodes with no incoming edges that are not marked as entry (orphans): {string.Join(", ", unmarkedEntryNodes.Select(n => n.Name))}.");
            result.IsValid = false;
        }

        // Terminal node check: at least one node with no outgoing edges and IsTerminal=true
        var terminalNodeIds = action.Nodes
            .Select(n => n.Id)
            .Except(action.Edges.Select(e => e.SourceNodeId))
            .ToHashSet();

        var terminalNodes = action.Nodes.Where(n => terminalNodeIds.Contains(n.Id)).ToList();
        if (terminalNodes.Count == 0)
        {
            result.Errors.Add("No terminal node found (all nodes have outgoing edges).");
            result.IsValid = false;
        }
        else if (!terminalNodes.Any(n => n.IsTerminal))
        {
            result.Errors.Add($"No node is marked as terminal among leaf nodes: {string.Join(", ", terminalNodes.Select(n => n.Name))}.");
            result.IsValid = false;
        }

        // Orphan node check: every node must be reachable from a designated entry node
        var reachable = new HashSet<Guid>();
        var bfsQueue = new Queue<Guid>();

        foreach (var entry in designatedEntryNodes)
        {
            bfsQueue.Enqueue(entry.Id);
            reachable.Add(entry.Id);
        }

        while (bfsQueue.Count > 0)
        {
            var current = bfsQueue.Dequeue();
            if (adj.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (reachable.Add(neighbor))
                    {
                        bfsQueue.Enqueue(neighbor);
                    }
                }
            }
        }

        var orphanNodes = action.Nodes.Where(n => !reachable.Contains(n.Id)).ToList();
        if (orphanNodes.Count > 0)
        {
            result.Errors.Add($"Orphan nodes not reachable from any entry: {string.Join(", ", orphanNodes.Select(n => n.Name))}.");
            result.IsValid = false;
        }

        // FR-3 (action-dag-semantic-hardening): Control fan-out check.
        // A node with >1 outgoing Control edges is rejected by the durable engine
        // (ResolveSingleSuccessor) and by the publish gate; the legacy executor
        // runs fan-out but surfaces the advisory here as a warning.
        var fanOutNodes = action.Nodes
            .Where(n => action.Edges.Count(e => e.SourceNodeId == n.Id
                                              && e.EdgeType == ActionEdgeType.Control) > 1)
            .ToList();
        foreach (var n in fanOutNodes)
        {
            var count = action.Edges.Count(e => e.SourceNodeId == n.Id && e.EdgeType == ActionEdgeType.Control);
            var msg = $"Node '{n.Name}' has {count} outgoing Control edges (fan-out); " +
                      "the durable engine and publish gate reject this.";
            if (fanOutAsError)
            {
                result.Errors.Add(msg);
                result.IsValid = false;
            }
            else
            {
                result.Warnings.Add(msg);
            }
        }

        // OnFailure fan-out check: >1 outgoing OnFailure edges from a single node
        // is an error under the publish profile (V2: exactly-one-arm property).
        // The durable engine (ResolveOnFailureSuccessor) rejects fan-out at runtime,
        // but we catch it at validation time so invalid DAGs never reach execution.
        var onFailureFanOutNodes = action.Nodes
            .Where(n => action.Edges.Count(e => e.SourceNodeId == n.Id
                                              && e.EdgeType == ActionEdgeType.OnFailure) > 1)
            .ToList();
        foreach (var n in onFailureFanOutNodes)
        {
            var count = action.Edges.Count(e => e.SourceNodeId == n.Id && e.EdgeType == ActionEdgeType.OnFailure);
            var msg = $"Node '{n.Name}' has {count} outgoing OnFailure edges; " +
                      "at most one OnFailure arm is allowed per node (exactly-one-arm property, V2).";
            if (fanOutAsError)
            {
                result.Errors.Add(msg);
                result.IsValid = false;
            }
            else
            {
                result.Warnings.Add(msg);
            }
        }

        return result;
    }
}
