using System.Security.Cryptography;
using AZOA.WebAPI.Models.Quest;
using StringBuilder = System.Text.StringBuilder;
// Inside namespace AZOA.WebAPI.Services.Quest the bare `Quest` binds to the
// namespace, not the model type — alias it (mirrors DappCompositionManager).
using QuestDef = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>Stable content hash of a published quest's node/edge graph (bait-and-switch guard). See Managers/AGENTS.md §published-version-hash.</summary>
public static class QuestPublishedVersion
{
    /// <summary>Deterministic SHA-256 over the ordered node (type/name/config) + edge (endpoints/type/condition) shape. Runner-visible identity of the exact graph revision.</summary>
    public static string ComputeHash(QuestDef quest)
    {
        var sb = new StringBuilder();

        // Order-independent: sort nodes/edges by a stable key so an equivalent graph
        // always hashes identically regardless of list insertion order.
        var nodes = quest.Nodes
            .OrderBy(n => n.Id)
            .Select(n => $"N|{n.Id}|{(int)n.NodeType}|{n.Name}|{n.IsEntry}|{n.IsTerminal}|{n.Config}");
        foreach (var line in nodes)
            sb.Append(line).Append('\n');

        var edges = quest.Edges
            .OrderBy(e => e.SourceNodeId).ThenBy(e => e.TargetNodeId).ThenBy(e => (int)e.EdgeType)
            .Select(e => $"E|{e.SourceNodeId}|{e.TargetNodeId}|{(int)e.EdgeType}|{e.Condition}");
        foreach (var line in edges)
            sb.Append(line).Append('\n');

        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
