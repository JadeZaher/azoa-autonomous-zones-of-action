using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest.Predicates;
using QuestModel = AZOA.WebAPI.Models.Quest.Quest;

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
    /// <param name="allRunExecutions">
    /// EVERY execution in the run so far, keyed by node id — the scope for the
    /// run-scoped <c>run.&lt;nodeName&gt;</c> root (any prior node's output by name,
    /// not just direct-edge predecessors). May be empty for entry nodes.
    /// </param>
    /// <param name="avatarId">Run avatar: holon reads are owner-scoped to this avatar.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A result containing <c>Ok=true</c> + resolved JSON on success, or
    /// <c>Ok=false</c> + error message on any failure.
    /// </returns>
    public async Task<BindingResolveResult> TryResolveAsync(
        string? configJson,
        QuestNode node,
        QuestModel quest,
        IReadOnlyDictionary<Guid, QuestNodeExecution> upstreamExecutions,
        IReadOnlyDictionary<Guid, QuestNodeExecution> allRunExecutions,
        Guid avatarId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return BindingResolveResult.Pass(configJson);

        // Fast path: if no $from key present, skip the walk entirely.
        if (!configJson.Contains("\"$from\"", StringComparison.Ordinal))
            return BindingResolveResult.Pass(configJson);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(configJson);
        }
        catch (JsonException ex)
        {
            return BindingResolveResult.Fail($"$from resolver: config JSON is malformed: {ex.Message}");
        }

        if (root is null)
        {
            // null document is fine (empty config), nothing to resolve.
            return BindingResolveResult.Pass(configJson);
        }

        // Build one combined scope: "upstream.<nodeName>" (direct-edge sources)
        // + "run.<nodeName>" (any prior node in the run) + holons lazily by
        // "holon.<id>" on first demand.
        var upstreamScope = BuildUpstreamScope(node, quest, upstreamExecutions);
        foreach (var (key, value) in BuildRunScope(quest, allRunExecutions))
            upstreamScope[key] = value;
        var holonCache = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var (ok, error) = await ResolveNodeAsync(root, upstreamScope, holonCache, avatarId, ct);
        if (!ok)
            return BindingResolveResult.Fail(error!);

        return BindingResolveResult.Pass(root.ToJsonString());
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

    // ── Result type ─────────────────────────────────────────────────────────

    /// <summary>Result of <see cref="QuestConfigBindingResolver.TryResolveAsync"/>.</summary>
    public sealed record BindingResolveResult(bool Ok, string? ResolvedJson, string? Error)
    {
        public static BindingResolveResult Pass(string? json) => new(true, json, null);
        public static BindingResolveResult Fail(string error) => new(false, null, error);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds upstream scope: "upstream.&lt;nodeName&gt;" → parsed JSON element
    /// for each incoming-edge source that has non-empty Output.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildUpstreamScope(
        QuestNode node,
        QuestModel quest,
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
    /// Builds run scope: "run.&lt;nodeName&gt;" → parsed JSON element for EVERY
    /// node in the run whose execution has non-empty parseable Output (not just
    /// direct-edge sources). Mirrors <see cref="BuildUpstreamScope"/> over all
    /// executions. See Services/Quest/AGENTS.md §output-binding.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildRunScope(
        QuestModel quest,
        IReadOnlyDictionary<Guid, QuestNodeExecution> allRunExecutions)
    {
        var scope = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var runNode in quest.Nodes)
        {
            if (!allRunExecutions.TryGetValue(runNode.Id, out var exec)
                || string.IsNullOrWhiteSpace(exec.Output))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(exec.Output);
                scope[$"run.{runNode.Name}"] = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Unparseable output — skip it; a $from that references it will
                // fail at resolution time with a descriptive error.
            }
        }

        return scope;
    }

    /// <summary>
    /// Recursively walks the JSON tree and replaces <c>{"$from":"path"}</c>
    /// binding objects in place. Holons are loaded on first use and cached.
    /// Returns (true, null) on success; (false, error) on failure.
    /// </summary>
    private async Task<(bool Ok, string? Error)> ResolveNodeAsync(
        JsonNode node,
        Dictionary<string, JsonElement> upstreamScope,
        Dictionary<string, JsonElement> holonCache,
        Guid avatarId,
        CancellationToken ct)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonObject childObj && IsSingleBinding(childObj, out var path))
                {
                    // Resolve the binding and replace the property value.
                    var (resolved, pathError) = TryResolvePath(path!, upstreamScope, holonCache);
                    if (pathError is not null)
                    {
                        // holon might need async load
                        var (loaded, loadError) = await TryLoadHolonAsync(path!, holonCache, avatarId, ct);
                        if (!loaded) return (false, loadError);
                        (resolved, pathError) = TryResolvePath(path!, upstreamScope, holonCache);
                        if (pathError is not null) return (false, pathError);
                    }
                    obj[key] = JsonNode.Parse(resolved!.Value.GetRawText());
                }
                else if (child is not null)
                {
                    var (ok, err) = await ResolveNodeAsync(child, upstreamScope, holonCache, avatarId, ct);
                    if (!ok) return (false, err);
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
                    return (false, "$from as an array element is not supported (V1 restriction).");
                }
                if (elem is not null)
                {
                    var (ok, err) = await ResolveNodeAsync(elem, upstreamScope, holonCache, avatarId, ct);
                    if (!ok) return (false, err);
                }
            }
        }

        return (true, null);
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
    /// Returns (element, null) on success; (default, errorMessage) on failure.
    /// A non-null error for a holon path means the holon is not yet in cache —
    /// caller should call TryLoadHolonAsync then retry.
    /// </summary>
    private static (JsonElement? Resolved, string? Error) TryResolvePath(
        string path,
        Dictionary<string, JsonElement> upstreamScope,
        Dictionary<string, JsonElement> holonCache)
    {
        if (!GatePath.TryParse(path, out var segments, out var parseError))
            return (null, $"$from '{path}': invalid path — {parseError}");

        var root = segments[0];
        JsonElement element;

        if (root == "upstream")
        {
            if (segments.Count < 3)
                return (null, $"$from '{path}': upstream path requires at least 3 segments (upstream.<node>.<field>).");

            var scopeKey = $"upstream.{segments[1]}";
            if (!upstreamScope.TryGetValue(scopeKey, out element))
                return (null, $"$from '{path}': upstream node '{segments[1]}' not found in scope or has no output.");

            for (var i = 2; i < segments.Count; i++)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    return (null, $"$from '{path}': cannot read member '{segments[i]}' from a non-object at segment {i}.");
                if (!element.TryGetProperty(segments[i], out var child))
                    return (null, $"$from '{path}': member '{segments[i]}' not found.");
                element = child;
            }
            return (element, null);
        }

        if (root == "run")
        {
            if (segments.Count < 3)
                return (null, $"$from '{path}': run path requires at least 3 segments (run.<node>.<field>).");

            var scopeKey = $"run.{segments[1]}";
            if (!upstreamScope.TryGetValue(scopeKey, out element))
                return (null, $"$from '{path}': run node '{segments[1]}' not found in scope or has no output.");

            for (var i = 2; i < segments.Count; i++)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    return (null, $"$from '{path}': cannot read member '{segments[i]}' from a non-object at segment {i}.");
                if (!element.TryGetProperty(segments[i], out var child))
                    return (null, $"$from '{path}': member '{segments[i]}' not found.");
                element = child;
            }
            return (element, null);
        }

        if (root == "holon")
        {
            if (segments.Count < 3)
                return (null, $"$from '{path}': holon path requires at least 3 segments (holon.<guid>.<field>).");

            var holonKey = $"holon.{segments[1]}";
            if (!holonCache.TryGetValue(holonKey, out element))
                return (null, $"holon.{segments[1]} not in cache"); // signal: needs async load

            for (var i = 2; i < segments.Count; i++)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    return (null, $"$from '{path}': cannot read member '{segments[i]}' from a non-object at holon segment {i}.");
                if (!element.TryGetProperty(segments[i], out var child))
                    return (null, $"$from '{path}': holon member '{segments[i]}' not found.");
                element = child;
            }
            return (element, null);
        }

        return (null, $"$from '{path}': unsupported root '{root}'.");
    }

    /// <summary>
    /// Loads a holon by id from the path's second segment, owner-scopes it to
    /// <paramref name="avatarId"/>, and caches it under "holon.&lt;id&gt;".
    /// Returns (true, null) on success; (false, error) on failure.
    /// </summary>
    private async Task<(bool Loaded, string? Error)> TryLoadHolonAsync(
        string path,
        Dictionary<string, JsonElement> holonCache,
        Guid avatarId,
        CancellationToken ct)
    {
        if (!GatePath.TryParse(path, out var segments, out var parseError))
            return (false, $"$from '{path}': {parseError}");

        if (segments[0] != "holon")
            return (true, null); // upstream paths don't need holon loading

        var idStr = segments[1];
        var cacheKey = $"holon.{idStr}";

        if (holonCache.ContainsKey(cacheKey))
            return (true, null); // already loaded

        if (!Guid.TryParse(idStr, out var holonId))
            return (false, $"$from '{path}': holon id '{idStr}' is not a valid GUID.");

        // Scope the read to the acting avatar; the explicit AvatarId check below stays
        // as defense-in-depth (owner-STRICT — a $from binding may only name owned holons).
        var result = await _holonManager.GetAsync(holonId, avatarId);
        if (result.IsError || result.Result is null || result.Result.AvatarId != avatarId)
        {
            // Not-found indistinguishable from non-owned (privacy posture same as GateCheck).
            return (false, $"$from '{path}': holon '{idStr}' not found or not accessible.");
        }

        holonCache[cacheKey] = HolonStateJson(result.Result);
        return (true, null);
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
