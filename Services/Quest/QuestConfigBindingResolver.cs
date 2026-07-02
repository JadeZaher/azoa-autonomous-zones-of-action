using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest.Predicates;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Type-agnostic pre-pass that resolves <c>{"$from": "path"}</c> bindings in a
/// node config JSON before strict deserialization runs.
/// See Services/Quest/AGENTS.md §output-binding.
/// </summary>
public sealed class QuestConfigBindingResolver
{
    private readonly IHolonManager _holonManager;

    public QuestConfigBindingResolver(IHolonManager holonManager)
    {
        _holonManager = holonManager ?? throw new ArgumentNullException(nameof(holonManager));
    }

    /// <summary>
    /// Walks <paramref name="configJson"/> and resolves every <c>{"$from":"path"}</c>
    /// property-value binding against the runtime scope built from
    /// <paramref name="upstreamExecutions"/> and the holon manager.
    /// </summary>
    /// <param name="configJson">Raw node config JSON (may be null/empty).</param>
    /// <param name="node">The quest node being resolved (for edge/name lookup).</param>
    /// <param name="quest">The owning quest (for graph walk).</param>
    /// <param name="upstreamExecutions">
    /// Incoming-edge source executions keyed by node id. May be empty for entry nodes.
    /// </param>
    /// <param name="avatarId">Run avatar: holon reads are owner-scoped to this avatar.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="resolvedJson">
    /// On success: config JSON with all bindings substituted. On failure: the original json.
    /// </param>
    /// <param name="error">On failure: descriptive error naming the path.</param>
    /// <returns>True when all bindings resolved; false with <paramref name="error"/> set on any failure.</returns>
    public async Task<bool> TryResolveAsync(
        string? configJson,
        QuestNode node,
        Quest quest,
        IReadOnlyDictionary<Guid, QuestNodeExecution> upstreamExecutions,
        Guid avatarId,
        CancellationToken ct,
        out string? resolvedJson,
        out string error)
    {
        resolvedJson = configJson;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configJson))
            return true;

        // Fast path: if no $from key present, skip the walk entirely.
        if (!configJson.Contains("\"$from\"", StringComparison.Ordinal))
            return true;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(configJson);
        }
        catch (JsonException ex)
        {
            error = $"$from resolver: config JSON is malformed: {ex.Message}";
            return false;
        }

        if (root is null)
        {
            // null document is fine (empty config), nothing to resolve.
            return true;
        }

        // Build the scope lazily: upstream outputs by "upstream.<nodeName>" +
        // holons lazily by "holon.<id>" on first demand.
        var upstreamScope = BuildUpstreamScope(node, quest, upstreamExecutions);
        var holonCache = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var ok = await ResolveNodeAsync(root, upstreamScope, holonCache, avatarId, ct, out error);
        if (!ok)
            return false;

        resolvedJson = root.ToJsonString();
        return true;
    }

    /// <summary>
    /// Returns the set of binding paths present in <paramref name="configJson"/>
    /// at property-value positions only (never array elements). Also validates
    /// binding objects for extra-key violations (AC-1d iv) and array-element
    /// violations (AC-1d v).
    /// Returns null on success; error string on any structural violation.
    /// </summary>
    public static string? FindAndValidateBindings(string? configJson, out List<string> paths)
    {
        paths = [];
        if (string.IsNullOrWhiteSpace(configJson))
            return null;
        if (!configJson.Contains("\"$from\"", StringComparison.Ordinal))
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(configJson);
        }
        catch (JsonException ex)
        {
            return $"config JSON is malformed: {ex.Message}";
        }

        if (root is null) return null;
        return CollectBindings(root, inArray: false, paths);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds upstream scope: "upstream.&lt;nodeName&gt;" → parsed JSON element
    /// for each incoming-edge source that has non-empty Output.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildUpstreamScope(
        QuestNode node,
        Quest quest,
        IReadOnlyDictionary<Guid, QuestNodeExecution> upstreamExecutions)
    {
        var scope = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var incomingSourceIds = quest.Edges
            .Where(e => e.TargetNodeId == node.Id)
            .Select(e => e.SourceNodeId)
            .ToHashSet();

        foreach (var sourceNode in quest.Nodes.Where(n => incomingSourceIds.Contains(n.Id)))
        {
            if (!upstreamExecutions.TryGetValue(sourceNode.Id, out var exec)
                || string.IsNullOrWhiteSpace(exec.Output))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(exec.Output);
                scope[$"upstream.{sourceNode.Name}"] = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Unparseable upstream output — skip it; a $from that references
                // it will fail at resolution time with a descriptive error.
            }
        }

        return scope;
    }

    /// <summary>
    /// Recursively walks the JSON tree and replaces <c>{"$from":"path"}</c>
    /// binding objects in place. Holons are loaded on first use and cached.
    /// </summary>
    private async Task<bool> ResolveNodeAsync(
        JsonNode node,
        Dictionary<string, JsonElement> upstreamScope,
        Dictionary<string, JsonElement> holonCache,
        Guid avatarId,
        CancellationToken ct,
        out string error)
    {
        error = string.Empty;

        if (node is JsonObject obj)
        {
            // Check if this object IS itself a binding ({"$from":"path"}).
            // We can't replace it in-place here — parent must do the swap.
            // So the parent calls IsSingleBinding before recursing into children.
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonObject childObj && IsSingleBinding(childObj, out var path))
                {
                    // Resolve the binding and replace the property value.
                    if (!TryResolvePath(path!, upstreamScope, holonCache, out var resolved, out error))
                    {
                        // holon might need async load
                        var loaded = await TryLoadHolonAsync(path!, holonCache, avatarId, ct, out error);
                        if (!loaded) return false;
                        if (!TryResolvePath(path!, upstreamScope, holonCache, out resolved, out error))
                            return false;
                    }
                    obj[key] = JsonNode.Parse(resolved!.GetRawText());
                }
                else if (child is not null)
                {
                    var ok = await ResolveNodeAsync(child, upstreamScope, holonCache, avatarId, ct, out error);
                    if (!ok) return false;
                }
            }
        }
        else if (node is JsonArray arr)
        {
            // Arrays: recurse into elements but do NOT resolve binding objects
            // inside arrays (V1: $from is legal as property value only, never
            // as array element). We still recurse into nested objects within
            // arrays so that object properties therein can be resolved.
            for (var i = 0; i < arr.Count; i++)
            {
                var elem = arr[i];
                if (elem is JsonObject elemObj && IsSingleBinding(elemObj, out _))
                {
                    // A $from directly as an array element is a definition-time
                    // error (AC-1d v). At runtime it should have been caught by
                    // FindAndValidateBindings. Fail closed if it slipped through.
                    error = "$from as an array element is not supported (V1 restriction).";
                    return false;
                }
                if (elem is not null)
                {
                    var ok = await ResolveNodeAsync(elem, upstreamScope, holonCache, avatarId, ct, out error);
                    if (!ok) return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="obj"/> is exactly <c>{"$from":"path"}</c>
    /// (exactly one key, string value, key == "$from").
    /// </summary>
    private static bool IsSingleBinding(JsonObject obj, out string? path)
    {
        path = null;
        if (obj.Count != 1) return false;
        var pair = obj.FirstOrDefault();
        if (pair.Key != "$from") return false;
        path = pair.Value?.GetValue<string>();
        return path is not null;
    }

    /// <summary>
    /// Resolves a path against the already-built upstream scope and holon cache.
    /// Returns false (with error) when the path is unknown or navigation fails.
    /// </summary>
    private static bool TryResolvePath(
        string path,
        Dictionary<string, JsonElement> upstreamScope,
        Dictionary<string, JsonElement> holonCache,
        out JsonElement resolved,
        out string error)
    {
        resolved = default;
        error = string.Empty;

        if (!GatePath.TryParse(path, out var segments, out var parseError))
        {
            error = $"$from '{path}': invalid path — {parseError}";
            return false;
        }

        var root = segments[0];
        JsonElement element;

        if (root == "upstream")
        {
            // "upstream.<nodeName>" is the scope key; remaining segments navigate JSON.
            if (segments.Count < 3)
            {
                error = $"$from '{path}': upstream path requires at least 3 segments (upstream.<node>.<field>).";
                return false;
            }
            var scopeKey = $"upstream.{segments[1]}";
            if (!upstreamScope.TryGetValue(scopeKey, out element))
            {
                error = $"$from '{path}': upstream node '{segments[1]}' not found in scope or has no output.";
                return false;
            }
            // Navigate remaining segments into the JSON.
            for (var i = 2; i < segments.Count; i++)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = $"$from '{path}': cannot read member '{segments[i]}' from a non-object at segment {i}.";
                    return false;
                }
                if (!element.TryGetProperty(segments[i], out var child))
                {
                    error = $"$from '{path}': member '{segments[i]}' not found.";
                    return false;
                }
                element = child;
            }
            resolved = element;
            return true;
        }

        if (root == "holon")
        {
            if (segments.Count < 3)
            {
                error = $"$from '{path}': holon path requires at least 3 segments (holon.<guid>.<field>).";
                return false;
            }
            var holonKey = $"holon.{segments[1]}";
            if (!holonCache.TryGetValue(holonKey, out element))
            {
                // Not yet loaded — caller must trigger async load.
                error = $"holon.{segments[1]} not in cache";
                return false;
            }
            for (var i = 2; i < segments.Count; i++)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = $"$from '{path}': cannot read member '{segments[i]}' from a non-object at holon segment {i}.";
                    return false;
                }
                if (!element.TryGetProperty(segments[i], out var child))
                {
                    error = $"$from '{path}': holon member '{segments[i]}' not found.";
                    return false;
                }
                element = child;
            }
            resolved = element;
            return true;
        }

        error = $"$from '{path}': unsupported root '{root}'.";
        return false;
    }

    /// <summary>
    /// Loads a holon by the id in the path's second segment, owner-scopes it to
    /// <paramref name="avatarId"/>, and caches it under "holon.&lt;id&gt;".
    /// Returns false (with error) if not found, unowned, or the path is invalid.
    /// </summary>
    private async Task<bool> TryLoadHolonAsync(
        string path,
        Dictionary<string, JsonElement> holonCache,
        Guid avatarId,
        CancellationToken ct,
        out string error)
    {
        error = string.Empty;

        if (!GatePath.TryParse(path, out var segments, out var parseError))
        {
            error = $"$from '{path}': {parseError}";
            return false;
        }

        if (segments[0] != "holon")
            return true; // upstream paths don't need holon loading

        var idStr = segments[1];
        var cacheKey = $"holon.{idStr}";

        if (holonCache.ContainsKey(cacheKey))
            return true; // already loaded

        if (!Guid.TryParse(idStr, out var holonId))
        {
            error = $"$from '{path}': holon id '{idStr}' is not a valid GUID.";
            return false;
        }

        var result = await _holonManager.GetAsync(holonId);
        if (result.IsError || result.Result is null || result.Result.AvatarId != avatarId)
        {
            // Not-found indistinguishable from non-owned (privacy posture same as GateCheck).
            error = $"$from '{path}': holon '{idStr}' not found or not accessible.";
            return false;
        }

        holonCache[cacheKey] = HolonStateJson(result.Result);
        return true;
    }

    /// <summary>
    /// Flattens a holon's live state into a JSON element. Mirrors
    /// <c>GateCheckNodeHandler.HolonStateJson</c> exactly — same typed fields +
    /// Metadata overlay. See Services/Quest/AGENTS.md §output-binding.
    /// </summary>
    private static JsonElement HolonStateJson(IHolon holon)
    {
        var state = new Dictionary<string, object?>
        {
            ["id"] = holon.Id.ToString(),
            ["name"] = holon.Name,
            ["assetType"] = holon.AssetType,
            ["tokenId"] = holon.TokenId,
            ["chainId"] = holon.ChainId,
            ["isActive"] = holon.IsActive,
            ["parentHolonId"] = holon.ParentHolonId?.ToString(),
            ["avatarId"] = holon.AvatarId?.ToString(),
        };

        foreach (var (key, value) in holon.Metadata)
            state[key] = value;

        var json = JsonSerializer.Serialize(state);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── Static binding-structural validation (definition time) ──────────────

    /// <summary>
    /// Walks the JSON tree to collect all binding paths and detect structural
    /// violations. Returns null on success; error string on first violation.
    /// </summary>
    private static string? CollectBindings(JsonNode node, bool inArray, List<string> paths)
    {
        if (node is JsonObject obj)
        {
            // Is this object itself a binding?
            if (obj.Count == 1 && obj.ContainsKey("$from"))
            {
                if (inArray)
                    return "$from as an array element is not supported (V1 restriction).";

                var pathVal = obj["$from"]?.GetValue<string>();
                if (pathVal is null)
                    return "$from value must be a non-null string.";

                paths.Add(pathVal);
                return null;
            }

            // Binding with extra keys — ambiguous.
            if (obj.ContainsKey("$from") && obj.Count > 1)
                return $"$from binding object must have exactly one key; found {obj.Count} keys.";

            // Recurse into properties.
            foreach (var pair in obj)
            {
                if (pair.Value is null) continue;
                var err = CollectBindings(pair.Value, inArray: false, paths);
                if (err is not null) return err;
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var elem = arr[i];
                if (elem is null) continue;
                // Pass inArray: true so a $from object at the element level is caught.
                var err = CollectBindings(elem, inArray: true, paths);
                if (err is not null) return err;
            }
        }

        return null;
    }
}
