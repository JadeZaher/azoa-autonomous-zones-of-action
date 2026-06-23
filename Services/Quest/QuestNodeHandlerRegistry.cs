using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Builds a <see cref="QuestNodeType"/> → <see cref="IQuestNodeHandler"/> map
/// from the registered handlers. Throws on a duplicate <see cref="QuestNodeType"/>
/// so the "exactly one handler per type" invariant fails fast at startup
/// instead of silently shadowing a handler.
/// </summary>
public sealed class QuestNodeHandlerRegistry : IQuestNodeHandlerRegistry
{
    private readonly IReadOnlyDictionary<QuestNodeType, IQuestNodeHandler> _handlers;

    public QuestNodeHandlerRegistry(IEnumerable<IQuestNodeHandler> handlers)
    {
        var map = new Dictionary<QuestNodeType, IQuestNodeHandler>();
        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.NodeType, handler))
            {
                throw new InvalidOperationException(
                    $"Duplicate quest node handler for {handler.NodeType}: " +
                    $"{map[handler.NodeType].GetType().Name} and {handler.GetType().Name}. " +
                    "Exactly one handler per QuestNodeType is required.");
            }
        }
        _handlers = map;
    }

    public bool TryGet(QuestNodeType type, out IQuestNodeHandler handler)
        => _handlers.TryGetValue(type, out handler!);
}
