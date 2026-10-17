using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces.Actions;

/// <summary>Creates durable domain correlation before a handler can admit an effect.</summary>
public interface IRecoverableActionNodeHandler : IActionNodeHandler
{
    /// <summary>Builds the exact request identity without performing effects; the engine supplies the frozen-input hash.</summary>
    ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash);
}
