using System.Text.Json;
using Microsoft.Extensions.Logging;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;
using QuestNode = OASIS.WebAPI.Models.Quest.QuestNode;
using QuestEdge = OASIS.WebAPI.Models.Quest.QuestEdge;
using QuestNodeTemplate = OASIS.WebAPI.Models.Quest.QuestNodeTemplate;
using QuestTemplate = OASIS.WebAPI.Models.Quest.QuestTemplate;
using QuestTemplateNode = OASIS.WebAPI.Models.Quest.QuestTemplateNode;
using QuestTemplateEdge = OASIS.WebAPI.Models.Quest.QuestTemplateEdge;
using QuestNodeType = OASIS.WebAPI.Models.Quest.QuestNodeType;
using QuestEdgeType = OASIS.WebAPI.Models.Quest.QuestEdgeType;

namespace OASIS.WebAPI.Services.Quest;

/// <summary>
/// Instantiates a Quest from a QuestTemplate with parameters.
/// Validates template parameters, substitutes placeholders, and produces a valid Quest.
/// </summary>
public class QuestInstantiator : IQuestInstantiator
{
    private readonly OASISDbContext _dbContext;
    private readonly IQuestDagValidator _validator;
    private readonly ILogger<QuestInstantiator> _logger;

    public QuestInstantiator(
        OASISDbContext dbContext,
        IQuestDagValidator validator,
        ILogger<QuestInstantiator> logger)
    {
        _dbContext = dbContext;
        _validator = validator;
        _logger = logger;
    }

    public async Task<QuestEntity> InstantiateAsync(Guid templateId, string parametersJson, Guid avatarId)
    {
        var template = await _dbContext.QuestTemplates.FindAsync(templateId);
        if (template == null)
        {
            throw new InvalidOperationException($"QuestTemplate {templateId} not found.");
        }

        // Validate parameters against template's parameter schema
        ValidateParameters(parametersJson, template.Parameters);

        // Parse parameters
        using var paramsDoc = JsonDocument.Parse(parametersJson);
        var parameters = paramsDoc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.ToString());

        // Create new Quest. Status moved to QuestRun (see quest-temporal-fork-model ADR §2.2).
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Description = template.Description,
            AvatarId = avatarId,
            TemplateId = templateId,
            CreatedDate = DateTime.UtcNow
        };

        // Build slot-to-node mapping for edge resolution
        var slotToNodeId = new Dictionary<string, Guid>();

        // Instantiate template nodes
        foreach (var templateNode in template.Nodes)
        {
            var nodeTemplate = await _dbContext.QuestNodeTemplates.FindAsync(templateNode.NodeTemplateId);
            if (nodeTemplate == null)
            {
                throw new InvalidOperationException(
                    $"QuestNodeTemplate {templateNode.NodeTemplateId} (slot: {templateNode.SlotId}) not found.");
            }

            // Merge default config with param overrides
            var config = MergeConfigs(nodeTemplate.DefaultConfig, templateNode.ParamOverrides, parameters);

            // Per-node State moved to QuestNodeExecution (see quest-temporal-fork-model ADR §2.2).
            var node = new QuestNode
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                NodeTemplateId = nodeTemplate.Id,
                NodeType = nodeTemplate.NodeType,
                Name = nodeTemplate.Name,
                Config = config,
                IsEntry = templateNode.IsEntry,
                IsTerminal = templateNode.IsTerminal,
                ExecutionOrder = 0
            };

            slotToNodeId[templateNode.SlotId] = node.Id;
            quest.Nodes.Add(node);
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

            quest.Edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = sourceNodeId,
                TargetNodeId = targetNodeId,
                EdgeType = templateEdge.EdgeType
            });
        }

        // Validate the resulting DAG
        var validationResult = _validator.Validate(quest);
        if (!validationResult.IsValid)
        {
            var errors = string.Join("; ", validationResult.Errors);
            throw new InvalidOperationException(
                $"Instantiated quest has invalid DAG: {errors}");
        }

        return quest;
    }

    private static void ValidateParameters(string parametersJson, string schemaJson)
    {
        // Basic validation: ensure parameters JSON is valid and required properties are present
        // Full JSON Schema validation would require a library like JsonSchema.Net
        using var paramsDoc = JsonDocument.Parse(parametersJson);
        using var schemaDoc = JsonDocument.Parse(schemaJson);

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
