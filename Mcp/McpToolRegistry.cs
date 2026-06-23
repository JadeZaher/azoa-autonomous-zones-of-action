namespace AZOA.WebAPI.Mcp;

public sealed class McpToolRegistry
{
    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.Ordinal);

    public McpToolRegistry(IEnumerable<IMcpTool> tools)
    {
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Name, tool))
                throw new InvalidOperationException(
                    $"Duplicate MCP tool name '{tool.Name}' — tool names must be unique.");
        }
    }

    public IMcpTool? Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

    public IReadOnlyCollection<IMcpTool> All => _tools.Values;
}
