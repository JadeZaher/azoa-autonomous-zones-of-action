using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest.Predicates;
using QuestModel = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Publish-time executability validator: proves that each <c>$from</c> binding
/// input WILL be satisfiable at runtime — the source node is a guaranteed ancestor,
/// the referenced output field is declared, and (best-effort) a bound scalar leaf
/// matches the consuming config field's type. See Services/Quest/AGENTS.md
/// §executability-validation.
/// </summary>
public sealed class QuestDagExecutabilityValidator : IQuestDagExecutabilityValidator
{
    /// <summary>Publish-time DAG size caps — bound the O(N²·E) dominator pass against
    /// a hostile mega-graph. See Services/Quest/AGENTS.md §executability-validation.</summary>
    private const int MaxNodes = 1000;
    private const int MaxEdges = 4000;

    /// <inheritdoc/>
    public DagValidationResult Validate(QuestModel quest)
    {
        var result = new DagValidationResult { IsValid = true };

        // Size gate FIRST: refuse to run the dominator dataflow on an unbounded graph
        // (cheap self-service CPU-exhaustion primitive otherwise, since any avatar can
        // publish their own quest).
        if (quest.Nodes.Count > MaxNodes || quest.Edges.Count > MaxEdges)
        {
            result.Errors.Add($"Quest too large to validate: {quest.Nodes.Count} nodes / " +
                            $"{quest.Edges.Count} edges exceeds the limit ({MaxNodes} nodes, {MaxEdges} edges).");
            result.IsValid = false;
            return result;
        }

        // Node-name uniqueness is a PRECONDITION for name-keyed `$from` addressing:
        // both `run.<name>`/`upstream.<name>` scopes are name-keyed, so a duplicate name
        // makes the validator (first-match) and the runtime resolver (last-writer-wins)
        // disagree on which node's output a binding resolves to. Reject up-front.
        var dupeNames = quest.Nodes
            .GroupBy(n => n.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupeNames.Count > 0)
        {
            foreach (var name in dupeNames)
                result.Errors.Add($"Duplicate node name '{name}': node names must be unique because " +
                                "'$from' bindings address nodes by name.");
            result.IsValid = false;
            return result;
        }

        // Guaranteed-ancestor sets keyed by node id (dominator dataflow).
        var dominators = ComputeDominators(quest);

        // Direct-predecessor node names per node id (runtime scope for `upstream.`).
        var directPredNames = quest.Nodes.ToDictionary(
            n => n.Id,
            n => quest.Edges
                .Where(e => e.TargetNodeId == n.Id)
                .Select(e => NameOf(quest, e.SourceNodeId))
                .Where(name => name is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal));

        foreach (var node in quest.Nodes)
        {
            // Collect (config-property-path, binding-path) pairs from the node config.
            var bindings = CollectBindingSites(node.Config);
            if (bindings.Count == 0) continue;

            var domNames = dominators.TryGetValue(node.Id, out var d)
                ? d.Select(id => NameOf(quest, id)).Where(x => x is not null).Cast<string>()
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var consumerConfigType = QuestNodeConfigRegistry.GetConfigType(node.NodeType);

            foreach (var (configPropPath, bindingPath) in bindings)
            {
                if (!GatePath.TryParse(bindingPath, out var segments, out _))
                    continue; // grammar errors are the config validator's job — don't double-report.

                var root = segments[0];

                // holon.<guid>.<field>: dynamic state, keep runtime fail-closed. Skip A/B/C.
                if (root == "holon") continue;

                // ── A. Reachability / guaranteed-ancestor ──
                var refName = segments[1];
                var refNode = quest.Nodes.FirstOrDefault(n =>
                    string.Equals(n.Name, refName, StringComparison.Ordinal));
                if (refNode is null)
                {
                    result.Errors.Add($"Node '{node.Name}': $from '{bindingPath}' references node " +
                                    $"'{refName}' which does not exist.");
                    continue;
                }

                if (root == "upstream")
                {
                    if (!directPredNames[node.Id].Contains(refName))
                    {
                        result.Errors.Add($"Node '{node.Name}': $from '{bindingPath}' — upstream node " +
                                        $"'{refName}' is not a direct predecessor (the runtime scope for " +
                                        "'upstream.' is incoming-edge sources only).");
                        continue;
                    }
                }
                else if (root == "run")
                {
                    if (!domNames.Contains(refName))
                    {
                        result.Errors.Add($"Node '{node.Name}': run node '{refName}' is not guaranteed to have " +
                                        $"executed before '{node.Name}' (it is not on every path reaching this " +
                                        "node); its output may be absent at runtime.");
                        continue;
                    }
                }

                // ── B. Field presence ──
                // A 2-segment path (root.name, no field) is a grammar error the config
                // validator rejects; guard here so this validator is safe when called
                // directly (tests) or if gate ordering ever changes.
                if (segments.Count < 3)
                    continue;

                var shape = QuestNodeOutputSchema.GetShape(refNode.NodeType);
                if (shape.Open)
                    continue; // opaque output — ancestry alone gates it.

                if (shape.Fields.Count == 0)
                {
                    result.Errors.Add($"Node '{node.Name}': node '{refName}' produces no readable output; " +
                                    $"'{bindingPath}' cannot resolve.");
                    continue;
                }

                // First post-name segment must match a declared field (case-insensitive).
                var fieldSeg = segments[2];
                var matched = shape.Fields.FirstOrDefault(f =>
                    string.Equals(f.Key, fieldSeg, StringComparison.OrdinalIgnoreCase));
                if (matched.Key is null)
                {
                    result.Errors.Add($"Node '{node.Name}': '{bindingPath}': field '{fieldSeg}' is not in the " +
                                    $"output of node '{refName}' (type {refNode.NodeType}).");
                    continue;
                }

                var fieldType = matched.Value;

                // Deep paths: if the matched field is Object/Array/Unknown, admit any
                // further segments (deep shape is not statically known). Only presence-
                // check the first level.
                var isDeep = segments.Count > 3;
                if (fieldType is OutputFieldType.Object or OutputFieldType.Array or OutputFieldType.Unknown)
                    continue; // admit; no further presence check, no type-check.

                // fieldType is a KNOWN scalar (String/Number/Boolean) at this point.
                // A deep path into a scalar is impossible at runtime, but we don't
                // over-reject — only the exact root.name.field scalar leaf is type-checked.
                if (isDeep) continue;

                // ── C. Best-effort scalar type match ──
                if (consumerConfigType is null) continue; // config-free consumer — nothing to check.

                var cfgFieldType = ResolveConfigFieldType(consumerConfigType, configPropPath);
                if (cfgFieldType is null) continue; // couldn't resolve the property — skip.

                // Only fire on a provable scalar-vs-scalar mismatch.
                if (IsScalar(fieldType) && IsScalar(cfgFieldType.Value) && fieldType != cfgFieldType.Value)
                {
                    result.Errors.Add($"Node '{node.Name}': '{bindingPath}' yields {fieldType} but config field " +
                                    $"'{configPropPath}' on node '{node.Name}' expects {cfgFieldType.Value}.");
                }
            }
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static bool IsScalar(OutputFieldType t) =>
        t is OutputFieldType.String or OutputFieldType.Number or OutputFieldType.Boolean;

    private static string? NameOf(QuestModel quest, Guid nodeId) =>
        quest.Nodes.FirstOrDefault(n => n.Id == nodeId)?.Name;

    // ── Dominator dataflow ──────────────────────────────────────────────────

    /// <summary>
    /// Computes, for each node, the set of GUARANTEED ancestors — nodes present on
    /// EVERY execution path from an entry node to it (dominators, EXCLUDING the node
    /// itself). Iterative forward dataflow over the topo order:
    /// <c>dom(n) = {n} ∪ (⋂ dom(p) for p in preds(n))</c>, entries seeded with {entry}.
    /// Small DAGs → a couple of passes converge. See §executability-validation.
    /// </summary>
    private static Dictionary<Guid, HashSet<Guid>> ComputeDominators(QuestModel quest)
    {
        var allIds = quest.Nodes.Select(n => n.Id).ToHashSet();
        // Dominator ancestry uses CONTROL edges only: they are the sole
        // unconditional-flow edge type. Conditional/OnFailure edges fire only when a
        // predicate passes / a source Failed, so a node reachable only via such an arm
        // is NOT guaranteed to have executed and must not count as a dominator.
        // See Services/Quest/AGENTS.md §executability-validation.
        var preds = quest.Nodes.ToDictionary(
            n => n.Id,
            n => quest.Edges.Where(e => e.TargetNodeId == n.Id
                            && e.EdgeType == QuestEdgeType.Control)
                            .Select(e => e.SourceNodeId)
                            .Where(allIds.Contains)
                            .ToHashSet());

        // TRUE entries = nodes with NO incoming edge of ANY type (real dataflow roots).
        // We must NOT infer entries from "zero Control-preds": a node reached only via a
        // Conditional/OnFailure arm also has zero Control-preds, and treating it as a
        // root would let it falsely dominate its own Control-successors — re-opening the
        // exact hole the Control-only filter closes.
        var anyIncoming = quest.Nodes.ToDictionary(
            n => n.Id,
            n => quest.Edges.Any(e => e.TargetNodeId == n.Id));
        var entryIds = quest.Nodes
            .Where(n => !anyIncoming[n.Id])
            .Select(n => n.Id)
            .ToHashSet();

        // Nodes Control-reachable from a true entry. A node NOT in this set is never
        // guaranteed to run (it's reached only via conditional/failure arms, or is
        // unreachable), so it must not appear in ANY node's guaranteed-ancestor set —
        // enforced by stripping it from every dom set after the fixpoint.
        var controlReachable = new HashSet<Guid>(entryIds);
        var bfs = new Queue<Guid>(entryIds);
        var controlSucc = quest.Nodes.ToDictionary(
            n => n.Id,
            n => quest.Edges.Where(e => e.SourceNodeId == n.Id && e.EdgeType == QuestEdgeType.Control)
                            .Select(e => e.TargetNodeId).Where(allIds.Contains).ToList());
        while (bfs.Count > 0)
        {
            var cur = bfs.Dequeue();
            foreach (var succ in controlSucc[cur])
                if (controlReachable.Add(succ)) bfs.Enqueue(succ);
        }

        // Initialize: entries dominate only themselves; everyone else = all nodes.
        var dom = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var id in allIds)
            dom[id] = entryIds.Contains(id)
                ? new HashSet<Guid> { id }
                : new HashSet<Guid>(allIds);

        bool changed = true;
        var guard = 0;
        while (changed && guard++ <= allIds.Count + 2)
        {
            changed = false;
            foreach (var node in quest.Nodes)
            {
                if (entryIds.Contains(node.Id)) continue;

                HashSet<Guid>? intersection = null;
                foreach (var p in preds[node.Id])
                {
                    if (intersection is null)
                        intersection = new HashSet<Guid>(dom[p]);
                    else
                        intersection.IntersectWith(dom[p]);
                }
                intersection ??= new HashSet<Guid>(); // no preds (unreachable): dominated by nothing.
                intersection.Add(node.Id);

                if (!intersection.SetEquals(dom[node.Id]))
                {
                    dom[node.Id] = intersection;
                    changed = true;
                }
            }
        }

        // Strip self (→ proper ancestors) AND any node not Control-reachable from a
        // true entry (those are never guaranteed to run, so they can't be a guaranteed
        // ancestor of anyone — even if they are a Control-pred of the consumer).
        foreach (var id in allIds)
        {
            dom[id].Remove(id);
            dom[id].IntersectWith(controlReachable);
        }
        return dom;
    }

    // ── Binding-site collection (config-property-path ↔ binding-path) ────────

    /// <summary>
    /// Walks a node config JSON and returns every <c>{"$from":"path"}</c> site as
    /// (dotted config-property path, binding path). The property path is the chain
    /// of object keys leading to the binding (array indices are skipped — bindings
    /// as array elements are already a definition-time error).
    /// </summary>
    private static List<(string ConfigPropPath, string BindingPath)> CollectBindingSites(string? configJson)
    {
        var sites = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(configJson)) return sites;
        if (!configJson.Contains("\"$from\"", StringComparison.Ordinal)) return sites;

        JsonNode? root;
        try { root = JsonNode.Parse(configJson); }
        catch (JsonException) { return sites; }
        if (root is null) return sites;

        Walk(root, "", sites);
        return sites;
    }

    private static void Walk(JsonNode node, string prefix, List<(string, string)> sites)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, child) in obj)
            {
                if (child is null) continue;
                if (child is JsonObject childObj
                    && childObj.Count == 1
                    && childObj.TryGetPropertyValue("$from", out var fromNode)
                    && fromNode is JsonValue fv
                    && fv.TryGetValue<string>(out var bindingPath))
                {
                    var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                    sites.Add((path, bindingPath));
                }
                else
                {
                    var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                    Walk(child, path, sites);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            // Recurse into nested objects; array-element bindings are already errors.
            foreach (var elem in arr)
                if (elem is not null) Walk(elem, prefix, sites);
        }
    }

    // ── Config-field CLR-type resolution ────────────────────────────────────

    /// <summary>
    /// Walks <paramref name="configType"/>'s properties along the dotted
    /// <paramref name="propPath"/> (case-insensitive, matching STJ's resolver) and
    /// maps the leaf CLR type to an <see cref="OutputFieldType"/>. Returns null when
    /// any segment can't be resolved (unknown property, indexer, etc.) — the caller
    /// then skips the type-check rather than risk a false positive.
    /// </summary>
    private static OutputFieldType? ResolveConfigFieldType(Type configType, string propPath)
    {
        var current = configType;
        var segments = propPath.Split('.');
        foreach (var seg in segments)
        {
            var prop = current.GetProperty(seg,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null) return null;
            current = prop.PropertyType;
        }
        return MapClrType(current);
    }

    /// <summary>Maps a CLR type to the output-field taxonomy (string/number/bool/array/object).</summary>
    private static OutputFieldType MapClrType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime))
            return OutputFieldType.String; // Guid/DateTime serialize as JSON strings.
        if (t == typeof(bool))
            return OutputFieldType.Boolean;
        if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
            || t == typeof(decimal) || t == typeof(double) || t == typeof(float)
            || t.IsEnum)
            return OutputFieldType.Number; // enums serialize as ints by default here.
        if (t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t))
            return OutputFieldType.Array;

        return OutputFieldType.Object;
    }
}
