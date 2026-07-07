using System.Text.Json;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
// Inside namespace AZOA.WebAPI.Services.Quest the bare `Quest` binds to the
// namespace, not the model type — alias it (mirrors DappCompositionManager).
using QuestDef = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Builds a <see cref="QuestEconomicManifest"/> from a quest's DAG: the value-moving
/// nodes a non-owner marketplace runner is about to auto-fire against themselves.
/// See Services/Quest/AGENTS.md §economic-manifest.
/// </summary>
public static class QuestEconomicManifestBuilder
{
    /// <summary>
    /// Node types that MOVE value. A node is treated as economic when its registered
    /// handler declares <c>RequiresChainCapability</c> OR its type is in this explicit
    /// set (defence-in-depth if a type ever ships without a registered handler).
    /// </summary>
    private static readonly HashSet<QuestNodeType> EconomicNodeTypes = new()
    {
        QuestNodeType.Swap, QuestNodeType.Grant, QuestNodeType.Transfer, QuestNodeType.Refund,
        QuestNodeType.FungibleTokenCreate, QuestNodeType.Bridge, QuestNodeType.Back,
        QuestNodeType.NftTransfer, QuestNodeType.NftMint, QuestNodeType.NftBurn,
    };

    /// <summary>True if this node moves value (registry capability flag OR the explicit economic set).</summary>
    public static bool IsEconomicNode(QuestNode node, IQuestNodeHandlerRegistry registry) =>
        EconomicNodeTypes.Contains(node.NodeType)
        || (registry.TryGet(node.NodeType, out var handler) && handler.RequiresChainCapability);

    /// <summary>Compute the manifest for a quest in topological (ExecutionOrder) order.</summary>
    public static QuestEconomicManifest Build(QuestDef quest, IQuestNodeHandlerRegistry registry, string? versionHash)
    {
        var manifest = new QuestEconomicManifest
        {
            QuestId = quest.Id,
            PublishedVersionHash = versionHash,
        };

        foreach (var node in quest.Nodes.OrderBy(n => n.ExecutionOrder))
        {
            if (!IsEconomicNode(node, registry)) continue;
            var (destination, amount) = ExtractDisclosure(node);
            manifest.Entries.Add(new QuestEconomicManifestEntry
            {
                NodeId = node.Id,
                NodeName = node.Name,
                NodeType = node.NodeType,
                Destination = destination,
                Amount = amount,
            });
        }

        return manifest;
    }

    /// <summary>
    /// Best-effort destination/amount extraction from a node's raw config JSON. Never
    /// throws — an unparseable config yields (null, null) but the node still appears in
    /// the manifest so its PRESENCE is disclosed even if its params are opaque.
    /// </summary>
    private static (string? destination, string? amount) ExtractDisclosure(QuestNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Config)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(node.Config);
            var root = doc.RootElement;
            return (FindFirstString(root, DestinationKeys), FindFirstString(root, AmountKeys));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static readonly string[] DestinationKeys =
        { "recipientAddress", "sourceRecipientAddress", "targetChain", "toAddress", "recipient", "destination" };

    private static readonly string[] AmountKeys = { "amount", "total", "quantity" };

    /// <summary>Recursively find the first property (case-insensitive) matching any key, returned as a string.</summary>
    private static string? FindFirstString(JsonElement element, string[] keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (keys.Any(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var scalar = AsScalar(prop.Value);
                    if (scalar != null) return scalar;
                }
                var nested = FindFirstString(prop.Value, keys);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstString(item, keys);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static string? AsScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };
}
