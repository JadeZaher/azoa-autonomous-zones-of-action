using System.Text.Json.Nodes;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions.Designer;

internal static class WorkflowDesignerResponseSchema
{
    public static JsonNode Create() => new JsonObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JsonArray("questionIds", "riskIds", "gapIds", "blockerIds", "planStages"),
        ["properties"] = new JsonObject
        {
            ["questionIds"] = EnumArray<WorkflowDesignerQuestion>(), ["riskIds"] = EnumArray<WorkflowDesignerRisk>(),
            ["gapIds"] = EnumArray<WorkflowDesignerDimension>(), ["blockerIds"] = EnumArray<WorkflowDesignerBlocker>(),
            ["planStages"] = EnumArray<WorkflowDesignerStage>()
        }
    };

    private static JsonNode EnumArray<T>() where T : struct, Enum => new JsonObject
    {
        ["type"] = "array", ["items"] = new JsonObject
        {
            ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames<T>().Select(value => JsonValue.Create(value)).ToArray())
        }
    };
}
