using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;
using ActionNode = AZOA.WebAPI.Models.Actions.ActionNode;
using ActionEdge = AZOA.WebAPI.Models.Actions.ActionEdge;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Instantiates a ActionDefinition from a ActionTemplate with parameters.
/// Validates template parameters, substitutes placeholders, and produces a valid ActionDefinition.
/// </summary>
public class ActionInstantiator : IActionInstantiator
{
    private readonly IActionTemplateStore _templateStore;
    private readonly IActionDagValidator _validator;

    public ActionInstantiator(
        IActionTemplateStore templateStore,
        IActionDagValidator validator)
    {
        _templateStore = templateStore;
        _validator = validator;
    }

    public async Task<ActionEntity> InstantiateAsync(Guid templateId, string parametersJson, Guid userId)
    {
        var template = await _templateStore.GetTemplateAsync(templateId, CancellationToken.None);
        if (template == null)
        {
            throw new InvalidOperationException($"ActionTemplate {templateId} not found.");
        }

        // Validate parameters against template's parameter schema
        ValidateParameters(parametersJson, template.Parameters);

        // Parse parameters. Duplicate object keys (System.Text.Json keeps both)
        // would throw out of ToDictionary -> reject them cleanly instead.
        using var paramsDoc = JsonDocument.Parse(parametersJson);
        var parameters = BuildParameters(paramsDoc.RootElement);

        // Create new ActionDefinition. Status moved to ActionRun (see action-temporal-fork-model ADR §2.2).
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Description = template.Description,
            UserId = userId,
            TemplateId = templateId,
            CreatedDate = DateTime.UtcNow
        };

        // Build slot-to-node mapping for edge resolution
        var slotToNodeId = new Dictionary<string, Guid>();

        // Instantiate template nodes
        foreach (var templateNode in template.Nodes)
        {
            var nodeTemplate = await _templateStore.GetNodeTemplateAsync(
                templateNode.NodeTemplateId, CancellationToken.None);
            if (nodeTemplate == null)
            {
                throw new InvalidOperationException(
                    $"ActionNodeTemplate {templateNode.NodeTemplateId} (slot: {templateNode.SlotId}) not found.");
            }

            // Merge default config with param overrides
            var config = MergeConfigs(nodeTemplate.DefaultConfig, templateNode.ParamOverrides, parameters);

            // Per-node State moved to ActionNodeExecution (see action-temporal-fork-model ADR §2.2).
            var node = new ActionNode
            {
                Id = Guid.NewGuid(),
                ActionId = action.Id,
                NodeTemplateId = nodeTemplate.Id,
                NodeType = nodeTemplate.NodeType,
                Name = nodeTemplate.Name,
                Config = config,
                IsEntry = templateNode.IsEntry,
                IsTerminal = templateNode.IsTerminal,
                ExecutionOrder = 0
            };

            slotToNodeId[templateNode.SlotId] = node.Id;
            action.Nodes.Add(node);
        }

        // Instantiate template edges
        foreach (var templateEdge in template.Edges)
        {
            if (!slotToNodeId.TryGetValue(templateEdge.SourceSlotId, out var sourceNodeId))
            {
                throw new InvalidOperationException(
                    $"Edge references unknown source slot: {templateEdge.SourceSlotId}.");
            }
            if (!slotToNodeId.TryGetValue(templateEdge.TargetSlotId, out var targetNodeId))
            {
                throw new InvalidOperationException(
                    $"Edge references unknown target slot: {templateEdge.TargetSlotId}.");
            }

            action.Edges.Add(new ActionEdge
            {
                Id = Guid.NewGuid(),
                ActionId = action.Id,
                SourceNodeId = sourceNodeId,
                TargetNodeId = targetNodeId,
                EdgeType = templateEdge.EdgeType
            });
        }

        // Validate the resulting DAG
        var validationResult = _validator.Validate(action);
        if (!validationResult.IsValid)
        {
            var errors = string.Join("; ", validationResult.Errors);
            throw new InvalidOperationException(
                $"Instantiated action has invalid DAG: {errors}");
        }

        return action;
    }

    private static void ValidateParameters(string parametersJson, string schemaJson)
    {
        // Basic validation: ensure parameters JSON is valid and required properties are present
        // Full JSON Schema validation would require a library like JsonSchema.Net
        using var paramsDoc = JsonDocument.Parse(parametersJson);
        using var schemaDoc = JsonDocument.Parse(schemaJson);

        // Reject duplicate parameter keys outright so validation (first-match
        // TryGetProperty) and the later dictionary build share one semantics.
        EnsureNoDuplicateKeys(paramsDoc.RootElement);

        if (schemaDoc.RootElement.TryGetProperty("required", out var required))
        {
            foreach (var reqProp in required.EnumerateArray())
            {
                var propName = reqProp.GetString();
                if (!paramsDoc.RootElement.TryGetProperty(propName!, out _))
                {
                    throw new InvalidOperationException(
                        $"Required parameter '{propName}' not provided.");
                }
            }
        }
    }

    /// <summary>Builds the param dictionary, rejecting duplicate object keys.</summary>
    private static Dictionary<string, string> BuildParameters(JsonElement root)
    {
        var parameters = new Dictionary<string, string>();
        if (root.ValueKind != JsonValueKind.Object) return parameters;
        foreach (var p in root.EnumerateObject())
        {
            if (!parameters.TryAdd(p.Name, p.ToString()))
                throw new InvalidOperationException($"Duplicate parameter key '{p.Name}' in parametersJson.");
        }
        return parameters;
    }

    /// <summary>Throws if the parameters object carries the same key twice.</summary>
    private static void EnsureNoDuplicateKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var seen = new HashSet<string>();
        foreach (var p in root.EnumerateObject())
        {
            if (!seen.Add(p.Name))
                throw new InvalidOperationException($"Duplicate parameter key '{p.Name}' in parametersJson.");
        }
    }

    private static string MergeConfigs(string defaultConfig, string paramOverrides, IReadOnlyDictionary<string, string> parameters)
    {
        // Start with default config
        var config = JsonSerializer.Deserialize<Dictionary<string, object?>>(defaultConfig)
            ?? new Dictionary<string, object?>();

        // Apply param overrides
        var overrides = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramOverrides)
            ?? new Dictionary<string, object?>();

        foreach (var (key, value) in overrides)
        {
            // Replace parameter placeholders like {{paramName}}
            var strValue = value?.ToString() ?? "";
            if (strValue.StartsWith("{{") && strValue.EndsWith("}}"))
            {
                var paramKey = strValue.Trim('{', '}');
                if (parameters.TryGetValue(paramKey, out var paramValue))
                {
                    config[key] = paramValue;
                }
            }
            else
            {
                config[key] = value;
            }
        }

        return JsonSerializer.Serialize(config);
    }
}
