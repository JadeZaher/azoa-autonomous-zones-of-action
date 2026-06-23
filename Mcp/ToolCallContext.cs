namespace AZOA.WebAPI.Mcp;

using Azoa.SurrealDb.Client.Query;

public sealed record ToolCallContext(
    Guid AvatarId,
    ISurrealExecutor Executor,
    IServiceProvider Services);
