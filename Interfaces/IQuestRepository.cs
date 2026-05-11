using OASIS.WebAPI.Models.Quest;

namespace OASIS.WebAPI.Interfaces;

/// <summary>
/// Persistence abstraction for Quest entities, wrapping OASISDbContext.
/// </summary>
public interface IQuestRepository
{
    // Quest CRUD
    Task<Quest?> GetByIdAsync(Guid id);
    Task<IEnumerable<Quest>> GetByAvatarIdAsync(Guid avatarId);
    Task<IEnumerable<Quest>> GetByDappSeriesIdAsync(Guid dappSeriesId);
    Task<Quest> CreateAsync(Quest quest);
    Task<Quest> UpdateAsync(Quest quest);
    Task<bool> DeleteAsync(Guid id);

    // Node templates
    Task<IEnumerable<QuestNodeTemplate>> GetNodeTemplatesAsync(bool? publicOnly = null);
    Task<QuestNodeTemplate?> GetNodeTemplateByIdAsync(Guid id);
    Task<QuestNodeTemplate> CreateNodeTemplateAsync(QuestNodeTemplate template);

    // Quest templates
    Task<IEnumerable<QuestTemplate>> GetQuestTemplatesAsync(bool? publicOnly = null);
    Task<QuestTemplate?> GetQuestTemplateByIdAsync(Guid id);
    Task<QuestTemplate> CreateQuestTemplateAsync(QuestTemplate template);
}
