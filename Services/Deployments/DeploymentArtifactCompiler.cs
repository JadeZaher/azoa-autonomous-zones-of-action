using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Services.Deployments;

/// <summary>Builds a bounded immutable composite graph and executable HTTP loader.</summary>
public sealed class DeploymentArtifactCompiler(IActionDagValidator validator)
{
    public RuntimeArtifact Compose(Guid bundleId, Guid owner, IReadOnlyList<DeploymentSource> sources,
        IReadOnlyList<DeploymentReference> references, IReadOnlyList<DeploymentInputMapping>? mappings)
    {
        if (sources.Count is < 1 or > 64 || sources.Sum(source => source.Snapshot.Nodes.Count) > 1000
            || sources.Select(source => source.Occurrence).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ArgumentException("A deployment requires 1–64 uniquely identified action occurrences and at most 1,000 nodes.");
        var graph = new ActionDefinition
        {
            Id = bundleId, UserId = owner, Name = "Deployed action graph", Status = ActionStatus.Active,
            CreatedDate = DateTime.UnixEpoch,
        };
        var ids = new Dictionary<(string Occurrence, Guid Node), Guid>();
        var names = new Dictionary<(string Occurrence, Guid Node), string>();
        var internalDependencies = new List<DeploymentInternalDependency>();
        var boundaries = new List<DeploymentOccurrenceBoundary>();
        foreach (var source in sources)
        {
            var check = validator.Validate(source.Snapshot);
            if (!check.IsValid) throw new ArgumentException(string.Join("; ", check.Errors));
            if (source.Snapshot.Nodes.Select(node => node.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Snapshot.Nodes.Count)
                throw new ArgumentException("Source node names must be unique for deterministic output binding.");
            foreach (var node in source.Snapshot.Nodes)
            {
                ids.Add((source.Occurrence, node.Id), DeploymentIdentity.GuidFor("deployment-node/2", source.Occurrence, source.ActionId.ToString("N"), node.Id.ToString("N")));
                names.Add((source.Occurrence, node.Id), $"o{sources.ToList().IndexOf(source)}_{node.Name}");
            }
        }
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var nameMap = source.Snapshot.Nodes.ToDictionary(node => node.Name,
                node => names[(source.Occurrence, node.Id)], StringComparer.OrdinalIgnoreCase);
            foreach (var node in source.Snapshot.Nodes)
            {
                var copy = JsonSerializer.Deserialize<ActionNode>(JsonSerializer.Serialize(node))!;
                copy.Id = ids[(source.Occurrence, node.Id)];
                copy.ActionId = graph.Id;
                copy.Name = names[(source.Occurrence, node.Id)];
                var config = JsonNode.Parse(copy.Config) ?? throw new ArgumentException("Node configuration is missing.");
                RewriteBindings(config, nameMap);
                if (copy.NodeType == ActionNodeType.GateCheck && config is JsonObject gate
                    && gate["predicate"] is JsonValue predicate && predicate.TryGetValue<string>(out var expression))
                    gate["predicate"] = RewriteCondition(expression, nameMap);
                copy.Config = config.ToJsonString();
                graph.Nodes.Add(copy);
            }
            foreach (var edge in source.Snapshot.Edges)
            {
                graph.Edges.Add(new ActionEdge
                {
                    Id = DeploymentIdentity.GuidFor("deployment-edge/2", source.Occurrence, edge.Id.ToString("N")),
                    ActionId = graph.Id, SourceNodeId = ids[(source.Occurrence, edge.SourceNodeId)],
                    TargetNodeId = ids[(source.Occurrence, edge.TargetNodeId)], EdgeType = edge.EdgeType,
                    Condition = RewriteCondition(edge.Condition, nameMap),
                });
            }
            foreach (var dependency in source.Snapshot.Dependencies)
            {
                var matches = sources.Where(item => item.ActionId == dependency.DependsOnActionId).ToList();
                if (matches.Count > 0)
                {
                    var depth = matches.Max(item => SharedOccurrenceDepth(source.Occurrence, item.Occurrence));
                    matches = matches.Where(item => SharedOccurrenceDepth(source.Occurrence, item.Occurrence) == depth).ToList();
                    if (matches.Count != 1 || sources.ToList().IndexOf(matches[0]) >= index)
                        throw new ArgumentException("An internal dependency must resolve to exactly one prior action occurrence.");
                    var prior = matches[0];
                    var required = dependency.DependsOnNodeId is { } nodeId
                        ? prior.Snapshot.Nodes.Where(node => node.Id == nodeId).ToArray() : prior.Snapshot.Nodes.ToArray();
                    if (required.Length == 0) throw new ArgumentException("Internal dependency node does not exist.");
                    foreach (var entryNode in source.Snapshot.Nodes.Where(node => node.IsEntry))
                        internalDependencies.Add(new DeploymentInternalDependency(ids[(source.Occurrence, entryNode.Id)],
                            required.Select(node => ids[(prior.Occurrence, node.Id)]).ToArray(), dependency.DependencyType,
                            prior.Occurrence, source.Occurrence));
                }
                else graph.Dependencies.Add(new ActionDependency
                {
                    Id = DeploymentIdentity.GuidFor("deployment-dependency/2", source.Occurrence, dependency.Id.ToString("N")),
                    ActionId = graph.Id, DependsOnActionId = dependency.DependsOnActionId,
                    DependsOnNodeId = dependency.DependsOnNodeId, DependencyType = dependency.DependencyType,
                });
            }
            if (index == 0) continue;
            var previous = sources[index - 1];
            foreach (var entry in source.Snapshot.Nodes.Where(node => node.IsEntry))
                boundaries.Add(new DeploymentOccurrenceBoundary(ids[(source.Occurrence, entry.Id)],
                    previous.Snapshot.Nodes.Select(node => ids[(previous.Occurrence, node.Id)]).ToArray()));
        }
        if ((mappings ?? []).GroupBy(mapping => (mapping.TargetOccurrence, mapping.TargetNodeId, mapping.TargetField)).Any(group => group.Count() > 1))
            throw new ArgumentException("Competing input mappings target the same occurrence, node and field.");
        foreach (var mapping in mappings ?? [])
        {
            if (!ids.TryGetValue((mapping.SourceOccurrence, mapping.SourceNodeId), out var sourceId)
                || !ids.TryGetValue((mapping.TargetOccurrence, mapping.TargetNodeId), out var targetId)
                || string.IsNullOrWhiteSpace(mapping.SourceField) || string.IsNullOrWhiteSpace(mapping.TargetField)
                || mapping.SourceField.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '.')
                || mapping.TargetField.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new ArgumentException("Invalid occurrence-specific input mapping.");
            var fromIndex = sources.ToList().FindIndex(source => source.Occurrence == mapping.SourceOccurrence);
            var toIndex = sources.ToList().FindIndex(source => source.Occurrence == mapping.TargetOccurrence);
            if (fromIndex >= toIndex) throw new ArgumentException("Cross-action mappings must reference a prior occurrence.");
            if (!DeploymentGuaranteedOutputs.Find(sources[fromIndex].Snapshot).Contains(mapping.SourceNodeId))
                throw new ArgumentException("Cross-action input requires an output guaranteed on successful occurrence completion.");
            var target = graph.Nodes.Single(node => node.Id == targetId);
            var config = JsonNode.Parse(target.Config) as JsonObject ?? throw new ArgumentException("Mapped node configuration must be an object.");
            config[mapping.TargetField] = new JsonObject { ["$from"] = $"run.{graph.Nodes.Single(node => node.Id == sourceId).Name}.{mapping.SourceField}" };
            target.Config = config.ToJsonString();
        }
        var final = validator.Validate(graph);
        if (!final.IsValid) throw new ArgumentException(string.Join("; ", final.Errors));
        for (var index = 0; index < final.TopologicalOrder.Count; index++)
            graph.Nodes.Single(node => node.Id == final.TopologicalOrder[index]).ExecutionOrder = index;
        graph.PublishedVersionHash = Services.Actions.ActionPublishedVersion.ComputeHash(graph);
        return new RuntimeArtifact(sources, graph, references, InternalDependencies: internalDependencies,
            InputMappings: mappings, OccurrenceBoundaries: boundaries);
    }

    private static int SharedOccurrenceDepth(string left, string right)
        => left.Split('/').SkipLast(1).Zip(right.Split('/').SkipLast(1)).TakeWhile(parts => parts.First == parts.Second).Count();

    public static (string PayloadJson, string PayloadHash, string? Loader, string ArtifactHash) Compile(DeploymentArtifactPayload payload)
    {
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(payload));
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new ArgumentException("Executable artifact exceeds 2 MiB.");
        var payloadHash = CanonicalJson.HashCanonical(json);
        var loader = payload.Runtime is null ? null : Loader(payloadHash);
        var artifactHash = CanonicalJson.Hash(new { version = 2, payloadHash, loaderHash = loader is null ? null : CanonicalJson.HashCanonical(loader) });
        return (json, payloadHash, loader, artifactHash);
    }

    private static string Loader(string payloadHash) => $$"""
        const expectedPayloadHash = '{{payloadHash}}';
        export function describe() { return { formatVersion: 2, expectedPayloadHash }; }
        export async function invoke({ baseUrl, deploymentId, accessToken, input = {}, consent = false, idempotencyKey }) {
          if (!deploymentId || !accessToken || !idempotencyKey) throw new Error('Deployment, authentication and request key are required.');
          const base = new URL(baseUrl);
          if (base.protocol !== 'https:' && !(base.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(base.hostname))) throw new Error('A secure API origin is required.');
          const response = await fetch(new URL('/api/deployments/' + encodeURIComponent(deploymentId) + '/invoke', base), {
            method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + accessToken, 'Idempotency-Key': idempotencyKey },
            body: JSON.stringify({ expectedPayloadHash, input, acknowledgeEconomicEffects: consent })
          });
          const result = await response.json();
          if (!response.ok || result.isError) throw new Error(result.message || 'Invocation was rejected.');
          return result.result;
        }
        """;

    private static void RewriteBindings(JsonNode node, IReadOnlyDictionary<string, string> names)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child is not null) RewriteBindings(child, names);
        }
        else if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Key is "$from" or "path" && property.Value is JsonValue value && value.TryGetValue<string>(out var path))
                {
                    var segments = path.Split('.', 3);
                    if (segments.Length >= 2 && segments[0] is "run" or "upstream" && names.TryGetValue(segments[1], out var replacement))
                        obj[property.Key] = segments[0] + "." + replacement + (segments.Length == 3 ? "." + segments[2] : "");
                }
                else if (property.Value is not null) RewriteBindings(property.Value, names);
            }
        }
    }

    private static string? RewriteCondition(string? condition, IReadOnlyDictionary<string, string> names)
    {
        if (string.IsNullOrWhiteSpace(condition)) return condition;
        if (condition.TrimStart().StartsWith('{') || condition.TrimStart().StartsWith('['))
        {
            var parsed = JsonNode.Parse(condition) ?? throw new ArgumentException("Conditional binding is empty.");
            RewriteBindings(parsed, names);
            return parsed.ToJsonString();
        }
        return PredicateBindingRewriter.Rewrite(condition, names);
    }
}
