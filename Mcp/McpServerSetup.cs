namespace OASIS.WebAPI.Mcp;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OASIS.WebAPI.Mcp.Tools;

/// <summary>
/// In-process MCP server hosted alongside OASIS.WebAPI at /mcp.
/// Uses Anthropic's official ModelContextProtocol.AspNetCore 1.3.0 SDK.
/// Tools self-register via DI: any class implementing IMcpTool that is
/// registered as IMcpTool gets picked up by McpToolRegistry's ctor.
/// </summary>
public static class McpServerSetup
{
    public static IServiceCollection AddMcpSurface(this IServiceCollection services)
    {
        services.AddSingleton<McpToolRegistry>();

        // ── Tool registrations (mcp-surface track) ────────────────────────────
        // Each tool is scoped: per-request lifetime so it can hold transient
        // state during ExecuteAsync. McpToolRegistry's singleton ctor receives
        // IEnumerable<IMcpTool> via root-scope resolution at startup; per-call
        // dispatch resolves the tool from the request scope.
        services.AddScoped<IMcpTool, QuestReachabilityTool>();
        services.AddScoped<IMcpTool, HolonTraverseTool>();
        services.AddScoped<IMcpTool, NftOwnershipGraphTool>();
        services.AddScoped<IMcpTool, AvatarScopedQueryTool>();
        services.AddScoped<IMcpTool, VectorSearchTool>();

        // ── Embedding provider (placeholder — see EmbeddingProvider.cs) ───────
        // Production deployments MUST swap DeterministicDummyEmbeddingProvider
        // for a real embedder (OpenAI, local Ollama, etc.) before vector_search
        // becomes semantically meaningful.
        services.AddSingleton<IEmbeddingProvider, DeterministicDummyEmbeddingProvider>();

        // Bridge to the SDK's MCP server with HTTP+SSE transport.
        services.AddMcpServer()
            .WithHttpTransport();
        return services;
    }

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
