using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Converts stored series data mappings to exact ordered occurrence identities.</summary>
public static class DeploymentSeriesMappings
{
    public static IReadOnlyList<DeploymentInputMapping> Resolve(IReadOnlyList<DappSeriesAction> entries, string prefix = "")
    {
        var result = new List<DeploymentInputMapping>();
        for (var targetIndex = 0; targetIndex < entries.Count; targetIndex++)
        {
            if (string.IsNullOrWhiteSpace(entries[targetIndex].InputMappings)) continue;
            var mappings = JsonSerializer.Deserialize<List<InputMapping>>(entries[targetIndex].InputMappings!, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new ArgumentException("Series input mappings must be an array.");
            foreach (var mapping in mappings)
            {
                var sources = Enumerable.Range(0, targetIndex).Where(index => entries[index].ActionIdGuid == mapping.SourceActionId).ToArray();
                if (sources.Length != 1 || mapping.TargetActionId != entries[targetIndex].ActionIdGuid)
                    throw new ArgumentException("Series input mapping does not identify one prior source occurrence and its target entry.");
                foreach (var (source, target) in mapping.FieldMap)
                    result.Add(new(prefix + sources[0].ToString(CultureInfo.InvariantCulture), mapping.SourceNodeId, source,
                        prefix + targetIndex.ToString(CultureInfo.InvariantCulture), mapping.TargetNodeId, target));
            }
        }
        return result;
    }
}
