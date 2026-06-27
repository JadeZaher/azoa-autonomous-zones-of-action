using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// Write tool for the "natural-language → DAG" flow. Accepts a fully-formed DAG
/// spec (built by the calling model from the <c>quest_catalog</c> vocabulary),
/// materializes it into a <see cref="QuestCreateModel"/>, and persists it via
/// <see cref="IQuestManager.CreateAsync"/> — which runs the authoritative
/// <c>QuestDagValidator</c>. On a validation failure the manager's message is
/// returned verbatim so the model can self-correct and retry.
///
/// No server-side LLM: the intelligence lives in the calling model. This tool
/// is the deterministic validate-and-persist seam, scoped to the calling avatar.
/// </summary>
public sealed class QuestAuthorTool : IMcpTool
{
    private const string InputSchemaJson =
        """
        {
          "type": "object",
          "properties": {
            "name":        { "type": "string", "minLength": 1, "maxLength": 256 },
            "description": { "type": "string" },
            "nodes": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "name":        { "type": "string" },
                  "node_type":   { "type": "string", "description": "A QuestNodeType name, e.g. HolonCreate. See quest_catalog." },
                  "config":      { "type": "string", "description": "JSON config string for the node; defaults to {}." },
                  "is_entry":    { "type": "boolean" },
                  "is_terminal": { "type": "boolean" }
                },
                "required": ["node_type"]
              }
            },
            "edges": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "source_node_id": { "type": "integer", "description": "Zero-based index into nodes[]." },
                  "target_node_id": { "type": "integer", "description": "Zero-based index into nodes[]." },
                  "edge_type":      { "type": "string", "description": "Control (default) or Conditional." },
                  "condition":      { "type": "string" }
                },
                "required": ["source_node_id", "target_node_id"]
              }
            }
          },
          "required": ["name", "nodes"]
        }
        """;

    private static readonly JsonElement _inputSchema;

    static QuestAuthorTool()
    {
        using var doc = JsonDocument.Parse(InputSchemaJson);
        _inputSchema = doc.RootElement.Clone();
    }

    public string Name        => "quest_author";
    public string Description => "Create a quest from a DAG spec (nodes + index-based edges). Validates the DAG structure and persists it for the calling avatar. Call quest_catalog first for the node vocabulary and rules.";
    public JsonElement InputSchema => _inputSchema;

    public async Task<JsonElement> ExecuteAsync(ToolCallContext context, JsonElement args, CancellationToken ct)
    {
        try
        {
            if (!args.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { Length: > 0 } name)
                return Error("name is required.");

            if (!args.TryGetProperty("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array || nodesEl.GetArrayLength() == 0)
                return Error("nodes is required and must be a non-empty array.");

            var model = new QuestCreateModel
            {
                Name = name,
                Description = args.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
            };

            // ── Parse nodes ──────────────────────────────────────────────
            var nodeCount = nodesEl.GetArrayLength();
            foreach (var nodeEl in nodesEl.EnumerateArray())
            {
                if (!nodeEl.TryGetProperty("node_type", out var typeEl) ||
                    !Enum.TryParse<QuestNodeType>(typeEl.GetString(), ignoreCase: false, out var nodeType))
                {
                    return Error($"Each node requires a valid node_type. Unknown or missing: '{(nodeEl.TryGetProperty("node_type", out var t) ? t.GetString() : null)}'. Call quest_catalog for valid names.");
                }

                model.Nodes.Add(new QuestNodeCreateModel
                {
                    Name = nodeEl.TryGetProperty("name", out var nm) ? nm.GetString() ?? nodeType.ToString() : nodeType.ToString(),
                    NodeType = nodeType,
                    Config = nodeEl.TryGetProperty("config", out var cfg) ? cfg.GetString() ?? "{}" : "{}",
                    IsEntry = nodeEl.TryGetProperty("is_entry", out var ie) && ie.ValueKind == JsonValueKind.True,
                    IsTerminal = nodeEl.TryGetProperty("is_terminal", out var it) && it.ValueKind == JsonValueKind.True,
                });
            }

            // ── Parse edges (index-based) ────────────────────────────────
            if (args.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var edgeEl in edgesEl.EnumerateArray())
                {
                    if (!edgeEl.TryGetProperty("source_node_id", out var srcEl) || srcEl.ValueKind != JsonValueKind.Number ||
                        !edgeEl.TryGetProperty("target_node_id", out var tgtEl) || tgtEl.ValueKind != JsonValueKind.Number)
                    {
                        return Error("Each edge requires integer source_node_id and target_node_id (indices into nodes[]).");
                    }

                    var src = srcEl.GetInt32();
                    var tgt = tgtEl.GetInt32();
                    if (src < 0 || src >= nodeCount || tgt < 0 || tgt >= nodeCount)
                        return Error($"Edge index out of range: {src}->{tgt}; nodes has {nodeCount} entries (valid indices 0..{nodeCount - 1}).");

                    var edgeType = QuestEdgeType.Control;
                    if (edgeEl.TryGetProperty("edge_type", out var etEl) &&
                        Enum.TryParse<QuestEdgeType>(etEl.GetString(), ignoreCase: false, out var parsed))
                    {
                        edgeType = parsed;
                    }

                    model.Edges.Add(new QuestEdgeCreateModel
                    {
                        SourceNodeId = src,
                        TargetNodeId = tgt,
                        EdgeType = edgeType,
                        Condition = edgeEl.TryGetProperty("condition", out var cond) ? cond.GetString() : null,
                    });
                }
            }

            // ── Persist, then validate the DAG ───────────────────────────
            // QuestManager.CreateAsync is intentionally permissive (it persists
            // drafts; DAG validity is enforced at activation via ValidateDAGAsync).
            // An MCP author wants immediate structural feedback, so we validate
            // here and roll back an invalid graph rather than leave a broken draft.
            var manager = context.Services.GetRequiredService<IQuestManager>();
            var result = await manager.CreateAsync(model, context.AvatarId);

            if (result.IsError || result.Result is null)
                return ToJsonElement(new { error = "create_failed", detail = result.Message });

            var quest = result.Result;

            var validation = await manager.ValidateDAGAsync(quest.Id);
            if (validation.IsError || !validation.Result)
            {
                // Roll back the invalid draft so a retry starts clean, and hand
                // the reason back so the calling model can fix the graph.
                await manager.DeleteAsync(quest.Id, context.AvatarId);
                return ToJsonElement(new { error = "validation_failed", detail = validation.Message });
            }

            return ToJsonElement(new
            {
                quest_id = quest.Id.ToString(),
                name = quest.Name,
                node_count = quest.Nodes.Count,
                edge_count = quest.Edges.Count,
            });
        }
        catch (Exception ex)
        {
            return Error("internal", ex.Message);
        }
    }

    private static JsonElement Error(string message, string? detail = null)
    {
        var obj = detail is null ? (object)new { error = message } : new { error = message, detail };
        return ToJsonElement(obj);
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        var raw = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
