using OASIS.WebAPI.Interfaces;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;
using QuestNode = OASIS.WebAPI.Models.Quest.QuestNode;
using QuestEdge = OASIS.WebAPI.Models.Quest.QuestEdge;
using QuestNodeState = OASIS.WebAPI.Models.Quest.QuestNodeState;
using QuestStatus = OASIS.WebAPI.Models.Quest.QuestStatus;

namespace OASIS.WebAPI.Services;

/// <summary>
/// Validates quest DAGs using Kahn's algorithm for cycle detection
/// and topological sort, plus entry/terminal/orphan checks.
/// </summary>
public class QuestDagValidator : IQuestDagValidator
{
    public DagValidationResult Validate(QuestEntity quest)
    {
        var result = new DagValidationResult { IsValid = true };

        if (quest.Nodes.Count == 0)
        {
            result.Errors.Add("Quest has no nodes.");
            result.IsValid = false;
            return result;
        }

        var nodeIds = quest.Nodes.Select(n => n.Id).ToHashSet();

        // Build adjacency list and in-degree count
        var adj = new Dictionary<Guid, List<Guid>>();
        var inDegree = new Dictionary<Guid, int>();
        var incomingEdges = new Dictionary<Guid, HashSet<Guid>>();

        foreach (var node in quest.Nodes)
        {
            adj[node.Id] = new List<Guid>();
            inDegree[node.Id] = 0;
            incomingEdges[node.Id] = new HashSet<Guid>();
        }

        foreach (var edge in quest.Edges)
        {
            if (!nodeIds.Contains(edge.SourceNodeId))
            {
                result.Errors.Add($"Edge {edge.Id}: SourceNodeId {edge.SourceNodeId} does not exist in quest nodes.");
                result.IsValid = false;
                continue;
            }
            if (!nodeIds.Contains(edge.TargetNodeId))
            {
                result.Errors.Add($"Edge {edge.Id}: TargetNodeId {edge.TargetNodeId} does not exist in quest nodes.");
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
                quest.Nodes.First(n => n.Id == id).Name).ToList();
            result.Errors.Add($"Cycle detected involving nodes: {string.Join(", ", cycleNodes)}.");
            result.IsValid = false;
            return result;
        }

        // Assign topological order
        for (int i = 0; i < topoOrder.Count; i++)
        {
            var node = quest.Nodes.First(n => n.Id == topoOrder[i]);
            node.ExecutionOrder = i;
        }

        result.TopologicalOrder = topoOrder;

        // Entry node check: nodes with no incoming edges that are marked as entry
        var nodesWithNoIncoming = quest.Nodes.Where(n => incomingEdges[n.Id].Count == 0).ToList();
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
        var terminalNodeIds = quest.Nodes
            .Select(n => n.Id)
            .Except(quest.Edges.Select(e => e.SourceNodeId))
            .ToHashSet();

        var terminalNodes = quest.Nodes.Where(n => terminalNodeIds.Contains(n.Id)).ToList();
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

        var orphanNodes = quest.Nodes.Where(n => !reachable.Contains(n.Id)).ToList();
        if (orphanNodes.Count > 0)
        {
            result.Errors.Add($"Orphan nodes not reachable from any entry: {string.Join(", ", orphanNodes.Select(n => n.Name))}.");
            result.IsValid = false;
        }

        return result;
    }
}
