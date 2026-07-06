using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Mcp;

public sealed record ToolCallContext(
    Guid AvatarId,
    ISurrealExecutor Executor,
    IServiceProvider Services);
