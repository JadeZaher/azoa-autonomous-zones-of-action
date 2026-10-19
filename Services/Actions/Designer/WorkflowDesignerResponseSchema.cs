using System.Text.Json.Nodes;

namespace AZOA.WebAPI.Services.Actions.Designer;

internal static class WorkflowDesignerResponseSchema
{
    public static JsonNode Create()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object", "additionalProperties": false,
              "required": ["summary", "questions", "concerns", "assumptions", "readyForDraft", "draft"],
              "properties": {
                "summary": { "type": "string" },
                "questions": { "type": "array", "items": { "type": "string" } },
                "concerns": { "type": "array", "items": { "type": "string" } },
                "assumptions": { "type": "array", "items": { "type": "string" } },
                "readyForDraft": { "type": "boolean" },
                "draft": { "anyOf": [
                  { "type": "object", "additionalProperties": false,
                    "required": ["name", "description", "isPublic", "nodes", "edges"],
                    "properties": {
                      "name": { "type": "string" },
                      "description": { "type": ["string", "null"] },
                      "isPublic": { "type": "boolean", "enum": [false] },
                      "nodes": { "type": "array", "items": {
                        "type": "object", "additionalProperties": false,
                        "required": ["name", "nodeType", "config", "isEntry", "isTerminal"],
                        "properties": {
                          "name": { "type": "string" }, "nodeType": { "type": "string" },
                          "config": { "type": "string" }, "isEntry": { "type": "boolean" }, "isTerminal": { "type": "boolean" }
                        }
                      } },
                      "edges": { "type": "array", "items": {
                        "type": "object", "additionalProperties": false,
                        "required": ["sourceNodeId", "targetNodeId", "edgeType", "condition"],
                        "properties": {
                          "sourceNodeId": { "type": "integer" }, "targetNodeId": { "type": "integer" },
                          "edgeType": { "type": "string", "enum": ["Control", "Conditional", "Data"] },
                          "condition": { "type": ["string", "null"] }
                        }
                      } }
                    }
                  },
                  { "type": "null" }
                ] }
              }
            }
            """)!;
        schema["properties"]!["draft"]!["anyOf"]![0]!["properties"]!["nodes"]!["items"]!["properties"]!["nodeType"]!["enum"] =
            new JsonArray(WorkflowDesignerPolicy.AllowedNodes.Select(node => JsonValue.Create(node.ToString())).ToArray());
        return schema;
    }
}
