namespace OASIS.WebAPI.Mcp;

using Oasis.SurrealDb.Client.Query;

public sealed record ToolCallContext(
    Guid AvatarId,
    ISurrealExecutor Executor,
    IServiceProvider Services);
