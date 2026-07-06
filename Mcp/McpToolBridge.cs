using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Mcp;

/// <summary>
/// Bridges the in-house <see cref="IMcpTool"/> registry to the ModelContextProtocol
/// SDK's tool dispatcher. Without this, <c>AddMcpServer().WithHttpTransport()</c>
/// serves the protocol envelope but advertises zero tools — <c>tools/list</c> is
/// empty and <c>tools/call</c> fails with "method not available". This adapter
/// wraps each registered <see cref="IMcpTool"/> as an <see cref="McpServerTool"/>
/// so they become reachable over the live <c>/mcp</c> transport.
///
/// Per-request avatar scoping: the SDK tool handler resolves the request-scoped
/// <see cref="IServiceProvider"/>, lifts the AvatarId that
/// <see cref="McpAuthMiddleware"/> stashed in <c>HttpContext.Items</c>, and builds
/// a <see cref="ToolCallContext"/> — identical to the context the integration
/// tests construct by hand.
/// </summary>
public static class McpToolBridge
{
    private const string AvatarItemKey = "mcp.avatar_id";

    /// <summary>
    /// Build an <see cref="McpServerTool"/> for a single <see cref="IMcpTool"/>.
    /// The tool's dynamic <see cref="IMcpTool.InputSchema"/> is patched onto the
    /// generated protocol tool so clients see the real argument schema.
    /// </summary>
    public static McpServerTool ToServerTool(IMcpTool tool, IServiceProvider? appServices = null)
    {
        // The SDK introspects the delegate's parameters for the input schema.
        // Our tools carry their own JSON Schema, so we take only the SDK-injected
        // RequestContext + cancellation token and overwrite the schema afterward.
        async ValueTask<CallToolResult> Handler(
            RequestContext<CallToolRequestParams> ctx,
            CancellationToken ct)
        {
            // Reassemble the raw args object from the SDK's name→JsonElement map.
            var args = ArgumentsToJsonElement(ctx.Params?.Arguments);

            // Resolve the request scope so tool execution uses request-scoped
            // services (ISurrealExecutor, IQuestManager, etc.).
            var http = ctx.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
            var requestServices = http?.RequestServices ?? ctx.Services ?? appServices;
            if (requestServices is null)
            {
                return ErrorResult("mcp_unavailable: no service provider in scope.");
            }

            // AvatarId is mandatory — McpAuthMiddleware already rejected (401)
            // any request without one, but guard defensively.
            if (http?.Items is null ||
                !http.Items.TryGetValue(AvatarItemKey, out var raw) ||
                raw is not Guid avatarId)
            {
                return ErrorResult("mcp_unauthorized: missing avatar scope.");
            }

            var executor = requestServices.GetRequiredService<ISurrealExecutor>();
            var toolContext = new ToolCallContext(avatarId, executor, requestServices);

            var result = await tool.ExecuteAsync(toolContext, args, ct);
            return JsonResult(result);
        }

        var options = new McpServerToolCreateOptions
        {
            Services = appServices,
            Name = tool.Name,
            Description = tool.Description,
        };

        var serverTool = McpServerTool.Create(Handler, options);

        // Overwrite the auto-generated (empty) schema with the tool's real one.
        serverTool.ProtocolTool.InputSchema = tool.InputSchema;
        return serverTool;
    }

    private static JsonElement ArgumentsToJsonElement(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        // Re-serialize the dictionary back into a single JSON object so the
        // existing IMcpTool implementations (which expect `args.TryGetProperty`)
        // work unchanged.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static CallToolResult JsonResult(JsonElement payload)
    {
        // Surface the tool's JSON both as text (human/LLM readable) and as
        // structured content (machine readable). IsError mirrors an `error` field
        // when the tool reported one, so MCP clients can branch on it.
        var isError = payload.ValueKind == JsonValueKind.Object &&
                      payload.TryGetProperty("error", out _);

        return new CallToolResult
        {
            Content = { new TextContentBlock { Text = payload.GetRawText() } },
            StructuredContent = payload,
            IsError = isError,
        };
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        Content = { new TextContentBlock { Text = $"{{\"error\":\"{message}\"}}" } },
        IsError = true,
    };
}
