using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IQuestManager
{
    // Quest CRUD
    Task<OASISResult<Quest>> CreateAsync(QuestCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<Quest>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null);

    // DAG validation
    Task<OASISResult<bool>> ValidateDAGAsync(Guid questId, OASISRequest? request = null);

    // Execution
    Task<OASISResult<Quest>> ExecuteAsync(Guid questId, OASISRequest? request = null);
    Task<OASISResult<QuestNode>> ExecuteNodeAsync(Guid questId, Guid nodeId, OASISRequest? request = null);

    // Templates
    Task<OASISResult<QuestTemplate>> CreateTemplateAsync(QuestTemplateCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<QuestTemplate>> GetTemplateAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<QuestTemplate>>> ListTemplatesAsync(OASISRequest? request = null);
    Task<OASISResult<Quest>> InstantiateTemplateAsync(Guid templateId, Guid avatarId, Dictionary<string, string>? parameters = null, OASISRequest? request = null);

    // Node Templates
    Task<OASISResult<QuestNodeTemplate>> CreateNodeTemplateAsync(QuestNodeTemplateCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(OASISRequest? request = null);
}
