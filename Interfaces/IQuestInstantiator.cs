using OASIS.WebAPI.Models.Quest;

namespace OASIS.WebAPI.Interfaces;

/// <summary>
/// Instantiates a Quest from a QuestTemplate with parameters.
/// </summary>
public interface IQuestInstantiator
{
    /// <summary>
    /// Instantiates a Quest from a QuestTemplate with the given parameters.
    /// </summary>
    /// <param name="templateId">The template to instantiate from.</param>
    /// <param name="parametersJson">JSON parameters matching the template's Parameters schema.</param>
    /// <param name="avatarId">The avatar that owns the new quest.</param>
    /// <returns>A new Quest with resolved nodes and edges.</returns>
    Task<Quest> InstantiateAsync(Guid templateId, string parametersJson, Guid avatarId);
}
