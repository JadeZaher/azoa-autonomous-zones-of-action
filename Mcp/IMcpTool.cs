namespace OASIS.WebAPI.Mcp;

using System.Text.Json;

public interface IMcpTool
{
    /// Snake-case identifier — e.g. "quest_reachability".
    string Name { get; }

    /// Human-readable description for tool discovery.
    string Description { get; }

    /// JSON Schema for the tool's input arguments.
    JsonElement InputSchema { get; }

    Task<JsonElement> ExecuteAsync(
        ToolCallContext context,
        JsonElement args,
        CancellationToken ct);
}
