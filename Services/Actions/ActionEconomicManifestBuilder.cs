using System.Text.Json;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
// Inside namespace AZOA.WebAPI.Services.Actions the bare `ActionDefinition` binds to the
// namespace, not the model type — alias it (mirrors DappCompositionManager).
using ActionDef = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Builds a <see cref="ActionEconomicManifest"/> from a action's DAG: the value-moving
/// nodes a non-owner marketplace runner is about to auto-fire against themselves.
/// See Services/ActionDefinition/AGENTS.md §economic-manifest.
/// </summary>
public static class ActionEconomicManifestBuilder
{
    /// <summary>
    /// Node types that MOVE value. A node is treated as economic when its registered
    /// handler declares <c>RequiresChainCapability</c> OR its type is in this explicit
    /// set (defence-in-depth if a type ever ships without a registered handler).
    /// </summary>
    private static readonly HashSet<ActionNodeType> EconomicNodeTypes = new()
    {
        ActionNodeType.Swap, ActionNodeType.Grant, ActionNodeType.Transfer, ActionNodeType.Refund,
        ActionNodeType.FungibleTokenCreate, ActionNodeType.Bridge, ActionNodeType.Back,
        ActionNodeType.NftTransfer, ActionNodeType.NftMint, ActionNodeType.NftBurn,
    };

    /// <summary>
    /// True if this node moves value. Fail-CLOSED: economic when the type is in the
    /// explicit set, OR its registered handler declares <c>RequiresChainCapability</c>,
    /// OR the type has NO registered handler at all (unclassifiable → treated as
    /// economic so a new/unregistered value-node cannot silently escape the consent
    /// disclosure). The consent gate is an allowlist of KNOWN-safe nodes, not a
    /// denylist of known-economic ones.
    /// </summary>
    public static bool IsEconomicNode(ActionNode node, IActionNodeHandlerRegistry registry)
    {
        if (EconomicNodeTypes.Contains(node.NodeType)) return true;
        if (!registry.TryGet(node.NodeType, out var handler)) return true; // unclassifiable ⇒ fail closed
        return handler.RequiresChainCapability;
    }

    /// <summary>Compute the manifest for a action in topological (ExecutionOrder) order.</summary>
    public static ActionEconomicManifest Build(ActionDef action, IActionNodeHandlerRegistry registry, string? versionHash)
    {
        var manifest = new ActionEconomicManifest
        {
            ActionId = action.Id,
            PublishedVersionHash = versionHash,
        };

        foreach (var node in action.Nodes.OrderBy(n => n.ExecutionOrder))
        {
            if (!IsEconomicNode(node, registry)) continue;
            var (destination, amount) = ExtractDisclosure(node);
            manifest.Entries.Add(new ActionEconomicManifestEntry
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
    private static (string? destination, string? amount) ExtractDisclosure(ActionNode node)
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
