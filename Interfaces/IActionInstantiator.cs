using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Instantiates an ActionDefinition from an ActionTemplate with parameters.
/// </summary>
public interface IActionInstantiator
{
    /// <summary>
    /// Instantiates an ActionDefinition from an ActionTemplate with the given parameters.
    /// </summary>
    /// <param name="templateId">The template to instantiate from.</param>
    /// <param name="parametersJson">JSON parameters matching the template's Parameters schema.</param>
    /// <param name="userId">The user that owns the new action.</param>
    /// <returns>A new ActionDefinition with resolved nodes and edges.</returns>
    Task<ActionDefinition> InstantiateAsync(Guid templateId, string parametersJson, Guid userId);
}
