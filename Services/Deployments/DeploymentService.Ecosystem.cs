using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    private async Task<string?> AppendEcosystemSourcesAsync(Guid bundleId, Guid actor, List<DeploymentSource> sources,
        List<DeploymentReference> references, List<DeploymentInputMapping> mappings, List<DeploymentBundleReference> bundleReferences, CancellationToken ct)
    {
        var ecosystem = await ecosystems.GetByActionGraphBundleAsync(bundleId, ct);
        if (ecosystem.IsError) return ecosystem.Message;
        if (ecosystem.Result is null) return null;
        if (ecosystem.Result.UserId != actor.ToString("N")) return "The ecosystem is not owned by this caller.";
        var loaded = await ecosystems.GetNodesAsync(Guid.ParseExact(ecosystem.Result.Id, "N"), ct);
        if (loaded.IsError || loaded.Result is null) return loaded.Message;
        var nodes = loaded.Result.OrderBy(node => node.CreatedDate).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        if (nodes.Length > 64 || nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            return "Ecosystem contains too many or duplicate nodes.";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in nodes.Where(node => string.IsNullOrEmpty(node.ParentNodeId)))
        {
            var error = await Walk(root, "ecosystem/", 1);
            if (error is not null) return error;
        }
        return visited.Count == nodes.Length ? null : "Ecosystem contains a cycle or disconnected parent reference.";

        async Task<string?> Walk(EcosystemNode node, string parentPath, int depth)
        {
            if (depth > 8 || !visited.Add(node.Id)) return "Ecosystem depth or cycle limit exceeded.";
            var path = parentPath + node.Id + "/";
            if (node.RefKind == EcosystemNode.RefKindValue.DappSeries)
            {
                var id = Guid.ParseExact(node.RefId, "N");
                var selected = await series.GetSeriesAsync(id, ct);
                if (selected.IsError || selected.Result is null || selected.Result.UserId != actor.ToString("N")) return "An ecosystem series is not owned by this caller.";
                var entries = await series.GetActionsBySeriesAsync(id, ct);
                if (entries.IsError || entries.Result is null) return entries.Message;
                var ordered = entries.Result.OrderBy(entry => entry.Order).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    var action = await actions.GetActionAsync(ordered[index].ActionIdGuid, ct);
                    if (action.IsError || action.Result is null || !CanRun(action.Result, actor) || action.Result.Status != ActionStatus.Active
                        || action.Result.PublishedVersionHash != ActionPublishedVersion.ComputeHash(action.Result)) return "An ecosystem source revision is not published and authorized.";
                    sources.Add(new(action.Result.Id, path + index.ToString(CultureInfo.InvariantCulture), action.Result.Version, action.Result.PublishedVersionHash!, action.Result));
                }
                try { mappings.AddRange(DeploymentSeriesMappings.Resolve(ordered, path)); }
                catch (Exception exception) when (exception is JsonException or ArgumentException) { return exception.Message; }
            }
            else
            {
                var id = Guid.ParseExact(node.RefId, "N");
                if (id == bundleId) return "An ecosystem cannot contain its owning bundle.";
                var nested = await bundles.GetByIdAsync(id, ct);
                if (nested.IsError || nested.Result is null || nested.Result.UserId != actor || !nested.Result.IsActive
                    || !TryBundleArtifact(nested.Result.DeploymentConfig, out var artifactHash)) return "A nested bundle needs a current generated artifact owned by this caller.";
                var artifact = await store.GetArtifactAsync(artifactHash, ct);
                if (artifact is null || artifact.ArtifactHash != artifactHash) return "Nested artifact identity is unavailable.";
                var payload = Read<DeploymentArtifactPayload>(artifact.PayloadJson);
                if (payload.Version != 2 || payload.BundleId != id || payload.Runtime is null || payload.Target.Kind != "PlatformRuntime")
                    return "A runtime ecosystem requires a generated runtime graph for each nested bundle.";
                if ((payload.Runtime.BundleReferences ?? []).Any(reference => reference.BundleId == bundleId)) return "Nested artifact references form a cycle.";
                var permitted = await CheckRuntimeAsync(payload.Runtime, actor, ct);
                if (permitted is not null) return permitted;
                foreach (var source in payload.Runtime.Sources)
                {
                    if (depth + source.Occurrence.Split('/').Count(part => Guid.TryParseExact(part, "N", out _)) > 8)
                        return "Nested artifact occurrence depth exceeds eight levels.";
                    sources.Add(source with { Occurrence = path + source.Occurrence });
                }
                references.AddRange(payload.Runtime.References);
                mappings.AddRange((payload.Runtime.InputMappings ?? []).Select(mapping => mapping with
                { SourceOccurrence = path + mapping.SourceOccurrence, TargetOccurrence = path + mapping.TargetOccurrence }));
                bundleReferences.Add(new(id, artifactHash));
                bundleReferences.AddRange(payload.Runtime.BundleReferences ?? []);
            }
            if (sources.Count > 64) return "Ecosystem exceeds the maximum of 64 action occurrences.";
            foreach (var child in nodes.Where(candidate => candidate.ParentNodeId == node.Id))
            {
                var error = await Walk(child, path, depth + 1);
                if (error is not null) return error;
            }
            return null;
        }
    }

    private static bool TryBundleArtifact(string? json, out string artifactHash)
    {
        artifactHash = "";
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var descriptor = JsonDocument.Parse(json);
            if (!descriptor.RootElement.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number != 2
                || !descriptor.RootElement.TryGetProperty("artifactHash", out var hash) || hash.ValueKind != JsonValueKind.String) return false;
            artifactHash = hash.GetString()!;
            return artifactHash.Length == 64;
        }
        catch (JsonException) { return false; }
    }
}
