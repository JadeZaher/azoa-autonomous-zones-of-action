using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <inheritdoc/>
public sealed class ActionNodeHandlerRegistry : IActionNodeHandlerRegistry
{
    private readonly IReadOnlyDictionary<ActionNodeType, IActionNodeHandler> _handlers;

    public ActionNodeHandlerRegistry(IEnumerable<IActionNodeHandler> handlers, ActionLocalMutationService? localMutations = null,
        ActionNftOperationService? nftOperations = null, ActionHoldOperationService? holdOperations = null)
    {
        var map = new Dictionary<ActionNodeType, IActionNodeHandler>();
        foreach (var handler in handlers)
        {
            IActionNodeHandler effective = localMutations is not null && ActionLocalMutationService.Supports(handler.NodeType)
                ? new ActionLocalMutationHandler(handler.NodeType, localMutations)
                : nftOperations is not null && ActionNftOperationService.Supports(handler.NodeType)
                    ? new ActionNftOperationHandler(handler, nftOperations)
                    : holdOperations is not null && ActionHoldOperationService.Supports(handler.NodeType)
                        ? new ActionHoldOperationHandler(handler, holdOperations)
                        : ActionReadRecovery.Supports(handler.NodeType) ? new ActionReadHandler(handler) : handler;
            if (!map.TryAdd(handler.NodeType, effective))
            {
                throw new InvalidOperationException(
                    $"Duplicate action node handler for {handler.NodeType}: " +
                    $"{map[handler.NodeType].GetType().Name} and {handler.GetType().Name}. " +
                    "Exactly one handler per ActionNodeType is required.");
            }
        }
        _handlers = map;
    }

    /// <inheritdoc/>
    public bool TryGet(ActionNodeType type, out IActionNodeHandler handler)
        => _handlers.TryGetValue(type, out handler!);
}
