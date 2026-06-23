using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Interfaces.QuestExecution;

/// <summary>
/// Resolves the <see cref="IQuestNodeHandler"/> for a <see cref="QuestNodeType"/>.
/// Built from an <see cref="IEnumerable{T}"/> of <see cref="IQuestNodeHandler"/>
/// keyed by <see cref="IQuestNodeHandler.NodeType"/> — one handler per type.
/// </summary>
public interface IQuestNodeHandlerRegistry
{
    /// <summary>Returns true and the handler for <paramref name="type"/>; false if none is registered.</summary>
    bool TryGet(QuestNodeType type, out IQuestNodeHandler handler);
}
