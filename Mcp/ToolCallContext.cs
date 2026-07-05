namespace AZOA.WebAPI.Mcp;

using SurrealForge.Client.Query;

public sealed record ToolCallContext(
    Guid AvatarId,
    ISurrealExecutor Executor,
    IServiceProvider Services);
