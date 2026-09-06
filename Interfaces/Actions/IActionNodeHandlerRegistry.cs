using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces.ActionExecution;

/// <summary>
/// Resolves the <see cref="IActionNodeHandler"/> for a <see cref="ActionNodeType"/>.
/// Built from an <see cref="IEnumerable{T}"/> of <see cref="IActionNodeHandler"/>
/// keyed by <see cref="IActionNodeHandler.NodeType"/> — one handler per type.
/// </summary>
public interface IActionNodeHandlerRegistry
{
    /// <summary>Returns true and the handler for <paramref name="type"/>; false if none is registered.</summary>
    bool TryGet(ActionNodeType type, out IActionNodeHandler handler);
}
