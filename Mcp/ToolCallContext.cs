using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Mcp;

public sealed record ToolCallContext(
    Guid UserId,
    ISurrealExecutor Executor,
    IServiceProvider Services);
