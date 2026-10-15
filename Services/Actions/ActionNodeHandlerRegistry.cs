using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Builds a <see cref="ActionNodeType"/> → <see cref="IActionNodeHandler"/> map
/// from the registered handlers. Throws on a duplicate <see cref="ActionNodeType"/>
/// so the "exactly one handler per type" invariant fails fast at startup
/// instead of silently shadowing a handler.
/// </summary>
public sealed class ActionNodeHandlerRegistry : IActionNodeHandlerRegistry
{
    private readonly IReadOnlyDictionary<ActionNodeType, IActionNodeHandler> _handlers;

    public ActionNodeHandlerRegistry(IEnumerable<IActionNodeHandler> handlers)
    {
        var map = new Dictionary<ActionNodeType, IActionNodeHandler>();
        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.NodeType, handler))
            {
                throw new InvalidOperationException(
                    $"Duplicate action node handler for {handler.NodeType}: " +
                    $"{map[handler.NodeType].GetType().Name} and {handler.GetType().Name}. " +
                    "Exactly one handler per ActionNodeType is required.");
            }
        }
        _handlers = map;
    }

    public bool TryGet(ActionNodeType type, out IActionNodeHandler handler)
        => _handlers.TryGetValue(type, out handler!);
}
