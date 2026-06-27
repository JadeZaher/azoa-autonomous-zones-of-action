namespace AZOA.WebAPI.Mcp;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using AZOA.WebAPI.Mcp.Tools;

/// <summary>
/// In-process MCP server hosted alongside AZOA.WebAPI at /mcp.
/// Uses Anthropic's official ModelContextProtocol.AspNetCore 1.3.0 SDK.
/// Tools self-register via DI: any class implementing IMcpTool that is
/// registered as IMcpTool gets picked up by McpToolRegistry's ctor.
/// </summary>
public static class McpServerSetup
{
    public static IServiceCollection AddMcpSurface(this IServiceCollection services)
    {
        // McpToolRegistry used to be Singleton, but it consumes
        // IEnumerable<IMcpTool> whose implementations are Scoped. ASP.NET
        // Core's BuildServiceProvider with ValidateOnBuild (the Development-
        // mode default) rejects that captive dependency. Scoped here keeps
        // both ends compatible -- the registry is just a Dictionary<string,
        // IMcpTool> over a handful of entries, cheap to rebuild per request.
        services.AddScoped<McpToolRegistry>();

        // ── Tool registrations (mcp-surface track) ────────────────────────────
        services.AddScoped<IMcpTool, QuestReachabilityTool>();
        // NL → DAG authoring surface: vocabulary (read) + validate-and-persist (write).
        services.AddScoped<IMcpTool, QuestCatalogTool>();
        services.AddScoped<IMcpTool, QuestAuthorTool>();
        services.AddScoped<IMcpTool, HolonTraverseTool>();
        services.AddScoped<IMcpTool, NftOwnershipGraphTool>();
        services.AddScoped<IMcpTool, AvatarScopedQueryTool>();
        services.AddScoped<IMcpTool, VectorSearchTool>();

        // ── Embedding provider (placeholder — see EmbeddingProvider.cs) ───────
        // Production deployments MUST swap DeterministicDummyEmbeddingProvider
        // for a real embedder (OpenAI, local Ollama, etc.) before vector_search
        // becomes semantically meaningful.
        services.AddSingleton<IEmbeddingProvider, DeterministicDummyEmbeddingProvider>();

        // McpAuthMiddleware stashes the avatar id on HttpContext.Items; the tool
        // bridge lifts it back out per request, so the accessor must be available.
        services.AddHttpContextAccessor();

        // Bridge to the SDK's MCP server with HTTP+SSE transport, and register
        // every IMcpTool as an SDK McpServerTool so they are actually reachable
        // over /mcp. The IMcpTool instances here are stateless shells — all
        // request-scoped dependencies (ISurrealExecutor, IQuestManager, …) are
        // resolved inside the bridge handler from the per-request service
        // provider via IHttpContextAccessor (see McpToolBridge), so no app
        // service provider is captured at registration time.
        var serverTools = ToolInstances()
            .Select(t => McpToolBridge.ToServerTool(t))
            .ToList();

        services.AddMcpServer()
            .WithHttpTransport()
            .WithTools(serverTools);
        return services;
    }

    /// <summary>
    /// The canonical IMcpTool set, instantiated as stateless shells for the SDK
    /// bridge. Mirrors the AddScoped registrations above (which back the
    /// integration-test ExecuteAsync path). Keep the two lists in sync.
    /// </summary>
    private static IEnumerable<IMcpTool> ToolInstances() => new IMcpTool[]
    {
        new QuestReachabilityTool(),
        new QuestCatalogTool(),
        new QuestAuthorTool(),
        new HolonTraverseTool(),
        new NftOwnershipGraphTool(),
        new AvatarScopedQueryTool(),
        new VectorSearchTool(),
    };

    public static IApplicationBuilder UseMcpAuth(this IApplicationBuilder app)
    {
        // Avatar-scope extraction middleware — extracts the AvatarId claim from
        // the authenticated HttpContext and stashes it on ctx.Items for the
        // MCP dispatcher to lift into ToolCallContext.
        return app.UseMiddleware<McpAuthMiddleware>();
    }

    public static IEndpointRouteBuilder MapMcp(this IEndpointRouteBuilder endpoints)
    {
        // SDK-provided MCP endpoint group. RequireAuthorization wires the
        // existing JWT+ApiKey multi-scheme policy so unauthenticated callers
        // get 401 at the framework layer. Per-tool avatar scoping is enforced
        // by McpAuthMiddleware (registered via UseMcpAuth) which populates
        // the ToolCallContext.AvatarId from claims.
        McpEndpointRouteBuilderExtensions.MapMcp(endpoints, "/mcp")
            .RequireAuthorization();
        return endpoints;
    }
}
